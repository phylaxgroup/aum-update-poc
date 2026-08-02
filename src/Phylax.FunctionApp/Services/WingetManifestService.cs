using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Phylax.FunctionApp.Services;

public class WingetManifestService
{
    private readonly ILogger<WingetManifestService> _log;
    private readonly HttpClient _http;
    private readonly int _cacheExpiryHours;

    private readonly Dictionary<string, CachedManifest> _cache = new();
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    private const string WingetManifestBase =
        "https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests";
    private const string WingetApiBase =
        "https://api.github.com/repos/microsoft/winget-pkgs/contents/manifests";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull
    };

    public WingetManifestService(
        ILogger<WingetManifestService> log,
        IHttpClientFactory httpClientFactory)
    {
        _log              = log;
        _http             = httpClientFactory.CreateClient("winget");
        _cacheExpiryHours = int.TryParse(
            Environment.GetEnvironmentVariable("CacheExpiryHours"), out var h) ? h : 24;
    }

    public async Task<WingetPackageInfo?> GetLatestVersionAsync(
        string wingetId,
        CancellationToken ct = default)
    {
        await _cacheLock.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(wingetId, out var cached) &&
                cached.ExpiresAt > DateTimeOffset.UtcNow)
            {
                _log.LogDebug("Cache hit for {WingetId}", wingetId);
                return cached.PackageInfo;
            }
        }
        finally
        {
            _cacheLock.Release();
        }

        var packageInfo = await FetchFromGitHubAsync(wingetId, ct);

        await _cacheLock.WaitAsync(ct);
        try
        {
            _cache[wingetId] = new CachedManifest
            {
                PackageInfo = packageInfo,
                ExpiresAt   = DateTimeOffset.UtcNow.AddHours(
                    _cacheExpiryHours > 0 ? _cacheExpiryHours : 0.1)
            };
        }
        finally
        {
            _cacheLock.Release();
        }

        return packageInfo;
    }

    private async Task<WingetPackageInfo?> FetchFromGitHubAsync(
        string wingetId,
        CancellationToken ct)
    {
        var parts = wingetId.Split('.', 2);
        if (parts.Length < 2)
        {
            _log.LogDebug("Invalid winget ID format: {Id}", wingetId);
            return null;
        }

        var publisher   = parts[0];
        var packageName = parts[1];
        var prefix      = publisher[0].ToString().ToLowerInvariant();

        var versionsUrl =
            $"{WingetApiBase}/{prefix}/{publisher}/{packageName}";

        _log.LogInformation(
            "Querying winget versions for {Id} at {Url}", wingetId, versionsUrl);

        try
        {
            var versionsResponse = await _http.GetAsync(versionsUrl, ct);
            if (!versionsResponse.IsSuccessStatusCode)
            {
                _log.LogWarning(
                    "Package not found in winget: {Id} (HTTP {Status}) URL: {Url}",
                    wingetId, (int)versionsResponse.StatusCode, versionsUrl);
                return null;
            }

            var versionsJson = await versionsResponse.Content.ReadAsStringAsync(ct);
            var versionDirs  = JsonSerializer.Deserialize<GitHubItem[]>(
                versionsJson, JsonOptions);

            if (versionDirs is null || versionDirs.Length == 0)
            {
                _log.LogWarning("No version directories found for {Id}", wingetId);
                return null;
            }

            var latestVersion = versionDirs
                .Where(d => d.Type == "dir")
                .Select(d => (Raw: d.Name, Parsed: TryParseVersion(d.Name)))
                .Where(x => x.Parsed is not null)
                .OrderByDescending(x => x.Parsed)
                .FirstOrDefault();

            if (latestVersion.Parsed is null)
            {
                _log.LogWarning("Could not determine latest version for {Id}", wingetId);
                return null;
            }

            _log.LogInformation(
                "Latest version for {Id}: {Version}", wingetId, latestVersion.Raw);

            var installer = await FetchInstallerAsync(
                prefix, publisher, packageName, latestVersion.Raw, ct);

            if (installer is null)
            {
                _log.LogWarning(
                    "No suitable installer found for {Id} {Version}",
                    wingetId, latestVersion.Raw);
                return null;
            }

            _log.LogInformation(
                "Selected installer for {Id}: Type={Type} Arch={Arch} Url={Url}",
                wingetId, installer.InstallerType, installer.Architecture,
                installer.InstallerUrl);

            return new WingetPackageInfo
            {
                WingetId        = wingetId,
                LatestVersion   = latestVersion.Raw,
                InstallerUrl    = installer.InstallerUrl,
                InstallerType   = installer.InstallerType,
                InstallerSha256 = installer.InstallerSha256,
                ProductCode     = installer.ProductCode,
                SilentArgs      = installer.InstallerSwitches?.Silent ?? string.Empty
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to fetch winget manifest for {Id}", wingetId);
            return null;
        }
    }

    private async Task<WingetInstaller?> FetchInstallerAsync(
        string prefix,
        string publisher,
        string packageName,
        string version,
        CancellationToken ct)
    {
        var manifestFile = $"{publisher}.{packageName}.installer.yaml";
        var manifestUrl  =
            $"{WingetManifestBase}/{prefix}/{publisher}/{packageName}" +
            $"/{version}/{manifestFile}";

        _log.LogInformation("Fetching installer manifest from {Url}", manifestUrl);

        var response = await _http.GetAsync(manifestUrl, ct);
        if (!response.IsSuccessStatusCode)
        {
            _log.LogWarning(
                "Installer manifest not found: HTTP {Status} {Url}",
                (int)response.StatusCode, manifestUrl);
            return null;
        }

        var yaml     = await response.Content.ReadAsStringAsync(ct);
        var manifest = ParseInstallerManifest(yaml);

        _log.LogInformation(
            "Parsed {Count} installer entries from manifest",
            manifest.Installers?.Count ?? 0);

        return SelectBestInstaller(manifest.Installers ?? []);
    }

    private static WingetInstaller? SelectBestInstaller(
        List<WingetInstaller> installers)
    {
        // Normalize wix -> msi (wix is the MSI installer type in newer manifests)
        foreach (var i in installers)
        {
            if (i.InstallerType.Equals("wix", StringComparison.OrdinalIgnoreCase))
                i.InstallerType = "msi";
        }

        // Preference: x64 MSI > x64 EXE > neutral MSI > anything
        return installers.FirstOrDefault(i =>
                i.Architecture.Equals("x64", StringComparison.OrdinalIgnoreCase) &&
                i.InstallerType.Equals("msi", StringComparison.OrdinalIgnoreCase))
            ?? installers.FirstOrDefault(i =>
                i.Architecture.Equals("x64", StringComparison.OrdinalIgnoreCase) &&
                i.InstallerType.Equals("exe", StringComparison.OrdinalIgnoreCase))
            ?? installers.FirstOrDefault(i =>
                i.InstallerType.Equals("msi", StringComparison.OrdinalIgnoreCase))
            ?? installers.FirstOrDefault(i =>
                i.InstallerType.Equals("exe", StringComparison.OrdinalIgnoreCase))
            ?? installers.FirstOrDefault();
    }

    private static WingetManifest ParseInstallerManifest(string yaml)
    {
        var manifest   = new WingetManifest();
        var installers = new List<WingetInstaller>();
        WingetInstaller? current = null;
        bool inInstallers        = false;
        bool inInstallerSwitches = false;

        foreach (var rawLine in yaml.Split('\n'))
        {
            var line    = rawLine.TrimEnd();
            var trimmed = line.TrimStart();
            var indent  = line.Length - trimmed.Length;

            if (trimmed.StartsWith("#") || string.IsNullOrWhiteSpace(trimmed))
                continue;

            // Detect start of Installers block
            if (trimmed == "Installers:")
            {
                inInstallers = true;
                continue;
            }

            if (!inInstallers) continue;

            // New installer entry — list item starting with dash
            // Newer manifests start with Architecture or InstallerLocale
            // Older manifests start with InstallerUrl
            if (trimmed.StartsWith("- ") && indent == 0)
            {
                current              = new WingetInstaller();
                inInstallerSwitches  = false;
                installers.Add(current);

                // Parse the field on the same line as the dash
                var afterDash = trimmed[2..];
                ParseInstallerField(current, afterDash);
                continue;
            }

            if (current is null) continue;

            // Detect InstallerSwitches nested block
            if (trimmed == "InstallerSwitches:")
            {
                inInstallerSwitches          = true;
                current.InstallerSwitches  ??= new WingetSwitches();
                continue;
            }

            // Exit InstallerSwitches if indent returns to installer level
            if (inInstallerSwitches && indent <= 2)
            {
                inInstallerSwitches = false;
            }

            if (inInstallerSwitches)
            {
                if (TryGetValue(trimmed, "Silent", out var silent))
                {
                    current.InstallerSwitches ??= new WingetSwitches();
                    current.InstallerSwitches.Silent = silent;
                }
                continue;
            }

            // Parse regular installer fields at any indent level
            ParseInstallerField(current, trimmed);
        }

        manifest.Installers = installers;
        return manifest;
    }

    private static void ParseInstallerField(WingetInstaller installer, string line)
    {
        if (TryGetValue(line, "Architecture", out var arch))
            installer.Architecture = arch;
        else if (TryGetValue(line, "InstallerType", out var type))
            installer.InstallerType = type;
        else if (TryGetValue(line, "InstallerUrl", out var url))
            installer.InstallerUrl = url;
        else if (TryGetValue(line, "InstallerSha256", out var hash))
            installer.InstallerSha256 = hash;
        else if (TryGetValue(line, "ProductCode", out var code))
        {
            // Clean up quoted GUIDs: '{23170F69-...}' -> {23170F69-...}
            var cleaned = code.Trim('\'', '"');
            installer.ProductCode = cleaned;
        }
    }

    private static bool TryGetValue(string line, string key, out string value)
    {
        value = string.Empty;
        var prefix = $"{key}:";
        if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        value = line[prefix.Length..].Trim().Trim('"').Trim('\'');
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetManifestLevelValue(
        string yaml, string key, out string value)
    {
        value = string.Empty;
        foreach (var line in yaml.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (line.Length > 0 && line[0] != ' ' && line[0] != '-')
            {
                if (TryGetValue(trimmed, key, out value))
                    return true;
            }
        }
        return false;
    }

    private static Version? TryParseVersion(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var cleaned = v.TrimStart('v', 'V');
        if (!cleaned.Contains('.')) cleaned += ".0";
        return Version.TryParse(cleaned, out var parsed) ? parsed : null;
    }
}

internal class CachedManifest
{
    public WingetPackageInfo? PackageInfo { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public class WingetPackageInfo
{
    public string WingetId { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;
    public string InstallerUrl { get; set; } = string.Empty;
    public string InstallerType { get; set; } = string.Empty;
    public string InstallerSha256 { get; set; } = string.Empty;
    public string? ProductCode { get; set; }
    public string SilentArgs { get; set; } = string.Empty;
}

internal class GitHubItem
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}

internal class WingetManifest
{
    public List<WingetInstaller>? Installers { get; set; }
}

internal class WingetInstaller
{
    public string InstallerUrl { get; set; } = string.Empty;
    public string InstallerSha256 { get; set; } = string.Empty;
    public string InstallerType { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string? ProductCode { get; set; }
    public WingetSwitches? InstallerSwitches { get; set; }
}

internal class WingetSwitches
{
    public string Silent { get; set; } = string.Empty;
}
