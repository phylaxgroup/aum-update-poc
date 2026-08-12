using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using Phylax.Connector.Configuration;
using Phylax.Connector.Models;

namespace Phylax.Connector.Services;

/// <summary>
/// Reads installed third-party software from the Windows registry
/// and attempts best-effort winget ID resolution for matched packages.
/// </summary>
[SupportedOSPlatform("windows")]
public class InventoryScanner
{
    private readonly ILogger<InventoryScanner> _log;
    private readonly PhylaxConnectorOptions _options;

    // Registry paths to check — 64-bit and 32-bit uninstall hives
    private static readonly string[] UninstallPaths =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    ];

    // Publishers to exclude — OS components, Microsoft runtimes, drivers
    private static readonly HashSet<string> ExcludedPublishers = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft Corporation",
        "Microsoft",
        "Windows",
        "Intel Corporation",
        "Intel(R) Corporation",
        "NVIDIA Corporation",
        "Advanced Micro Devices, Inc.",
        "AMD",
        "Realtek Semiconductor Corp.",
        "Dell Inc.",
        "HP Inc.",
        "Hewlett-Packard",
        "Lenovo"
    };

    // Display name fragments to exclude — system components
    private static readonly string[] ExcludedNameFragments =
    [
        "Microsoft Visual C++",
        "Microsoft .NET",
        "Windows SDK",
        "Windows Kits",
        "Microsoft Update",
        "Windows Update",
        "Update for Windows",
        "Security Update for",
        "Hotfix for",
        "Service Pack",
        "KB", 
        "Driver",
        "Redistributable",
        "Runtime",
        "DirectX"
    ];

    public InventoryScanner(
        ILogger<InventoryScanner> log,
        IOptions<PhylaxConnectorOptions> options)
    {
        _log     = log;
        _options = options.Value;
    }

    /// <summary>
    /// Scans the local machine's installed software and returns
    /// a normalized, filtered list of third-party applications.
    /// </summary>
    public List<InstalledApp> GetInstalledApplications()
    {
        _log.LogInformation("Starting installed software scan");

        var apps = new Dictionary<string, InstalledApp>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var path in UninstallPaths)
        {
            ScanRegistryHive(Registry.LocalMachine, path, apps);
        }

        var result = apps.Values
            .OrderBy(a => a.DisplayName)
            .ToList();

        _log.LogInformation(
            "Scan complete — {Total} third-party applications detected",
            result.Count);

        foreach (var app in result)
        {
            _log.LogDebug(
                "  Detected: {Name} {Version} | Publisher: {Publisher} | WingetId: {WingetId}",
                app.DisplayName,
                app.DisplayVersion,
                app.Publisher,
                app.WingetId ?? "unmatched");
        }

        return result;
    }

    private void ScanRegistryHive(
        RegistryKey hive,
        string path,
        Dictionary<string, InstalledApp> accumulator)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            if (key is null) return;

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                try
                {
                    using var subKey = key.OpenSubKey(subKeyName);
                    if (subKey is null) continue;

                    var app = ParseRegistryEntry(subKey);
                    if (app is null) continue;

                    if (accumulator.TryGetValue(app.DisplayName, out var existing))
                    {
                        if (IsRicherEntry(app, existing))
                            accumulator[app.DisplayName] = app;
                    }
                    else
                    {
                        accumulator[app.DisplayName] = app;
                    }
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Skipping registry subkey {Key} — read error", subKeyName);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read registry hive path {Path}", path);
        }
    }

    private InstalledApp? ParseRegistryEntry(RegistryKey key)
    {
        var displayName = key.GetValue("DisplayName") as string;
        if (string.IsNullOrWhiteSpace(displayName)) return null;

        var version = key.GetValue("DisplayVersion") as string;
        if (string.IsNullOrWhiteSpace(version)) return null;

        var publisher = key.GetValue("Publisher") as string ?? string.Empty;
        if (IsExcludedPublisher(publisher)) return null;
        if (IsExcludedName(displayName)) return null;

        var systemComponent = key.GetValue("SystemComponent") as int?;
        if (systemComponent == 1) return null;

        var uninstallString = key.GetValue("UninstallString") as string;
        if (string.IsNullOrWhiteSpace(uninstallString)) return null;

        DateTime? installDate = null;
        var installDateStr = key.GetValue("InstallDate") as string;
        if (!string.IsNullOrWhiteSpace(installDateStr) &&
            installDateStr.Length == 8 &&
            DateTime.TryParseExact(installDateStr, "yyyyMMdd",
                null, System.Globalization.DateTimeStyles.None,
                out var parsedDate))
        {
            installDate = parsedDate;
        }

        var app = new InstalledApp
        {
            DisplayName     = displayName.Trim(),
            DisplayVersion  = version.Trim(),
            Publisher       = publisher.Trim(),
            ProductCode     = ExtractProductCode(key.Name),
            InstallLocation = key.GetValue("InstallLocation") as string ?? string.Empty,
            InstallDate     = installDate
        };

        app.WingetId = TryResolveWingetId(app);

        return app;
    }

    private string? TryResolveWingetId(InstalledApp app)
    {
        var wingetPath = GetWingetPath();
        if (wingetPath is null) return null;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = wingetPath,
                Arguments              = $"search --name \"{EscapeWingetArg(app.DisplayName)}\" --source winget --accept-source-agreements",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15000);

            return ParseWingetSearchOutput(output, app.DisplayName);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex,
                "Winget resolution failed for {App} — will rely on server-side matching",
                app.DisplayName);
            return null;
        }
    }

    private static string? ParseWingetSearchOutput(string output, string appName)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var separatorIndex = Array.FindIndex(lines, l => l.TrimStart().StartsWith("---"));

        if (separatorIndex < 0 || separatorIndex >= lines.Length - 1)
            return null;

        string? bestId = null;
        double bestScore = 0;
        var normalizedAppName = NormalizeName(appName);

        for (int i = separatorIndex + 1; i < lines.Length; i++)
        {
            var parts = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;

            var idCandidate = parts.FirstOrDefault(
                p => Regex.IsMatch(p, @"^[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-\.]+$"));

            if (idCandidate is null) continue;

            var resultName = NormalizeName(lines[i]);
            var score = ComputeNameSimilarity(normalizedAppName, resultName);

            if (score > bestScore && score > 0.7)
            {
                bestScore = score;
                bestId    = idCandidate;
            }
        }

        return bestId;
    }

    private static double ComputeNameSimilarity(string a, string b)
    {
        var tokensA = new HashSet<string>(
            a.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);

        var tokensB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokensA.Count == 0 || tokensB.Length == 0) return 0;

        var matches = tokensB.Count(t => tokensA.Contains(t));
        return (double)matches / Math.Max(tokensA.Count, tokensB.Length);
    }

    private static string NormalizeName(string name)
    {
        return Regex.Replace(name, @"[\d\.\-\_\(\)\/]+", " ")
            .ToLowerInvariant()
            .Trim();
    }

    private static string? GetWingetPath()
    {
        var candidates = new[]
        {
            @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_*\winget.exe",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\WindowsApps\winget.exe")
        };

        foreach (var pattern in candidates)
        {
            if (pattern.Contains('*'))
            {
                var dir = Path.GetDirectoryName(pattern) ?? string.Empty;
                var parentDir = Path.GetDirectoryName(dir) ?? string.Empty;
                var dirPattern = Path.GetFileName(dir);

                if (Directory.Exists(parentDir))
                {
                    var match = Directory
                        .GetDirectories(parentDir, dirPattern)
                        .OrderByDescending(d => d)
                        .FirstOrDefault();

                    if (match is not null)
                    {
                        var candidate = Path.Combine(match, "winget.exe");
                        if (File.Exists(candidate)) return candidate;
                    }
                }
            }
            else if (File.Exists(pattern))
            {
                return pattern;
            }
        }

        return null;
    }

    private static string? ExtractProductCode(string keyName)
    {
        var match = Regex.Match(
            keyName,
            @"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}");

        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    private static string EscapeWingetArg(string value)
    {
        return value.Replace("\"", "").Replace("|", "").Replace("&", "");
    }

    private static bool IsExcludedPublisher(string publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return false;

        return ExcludedPublishers.Any(excluded =>
            publisher.StartsWith(excluded, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsExcludedName(string name)
    {
        return ExcludedNameFragments.Any(fragment =>
            name.StartsWith(fragment, StringComparison.OrdinalIgnoreCase) ||
            name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRicherEntry(InstalledApp candidate, InstalledApp existing)
    {
        int candidateScore = 0;
        int existingScore  = 0;

        if (!string.IsNullOrEmpty(candidate.ProductCode))   candidateScore++;
        if (!string.IsNullOrEmpty(candidate.WingetId))      candidateScore++;
        if (!string.IsNullOrEmpty(candidate.InstallLocation)) candidateScore++;
        if (candidate.InstallDate.HasValue)                 candidateScore++;

        if (!string.IsNullOrEmpty(existing.ProductCode))    existingScore++;
        if (!string.IsNullOrEmpty(existing.WingetId))       existingScore++;
        if (!string.IsNullOrEmpty(existing.InstallLocation)) existingScore++;
        if (existing.InstallDate.HasValue)                  existingScore++;

        return candidateScore > existingScore;
    }
}
