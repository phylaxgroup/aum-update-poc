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
        _log             = log;
        _http            = httpClientFactory.CreateClient("winget");
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
                ExpiresAt   = DateTimeOffset.UtcNow.AddHours(_cacheExpiryHours)
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

            var versionsJson = await versionsResponse.Content
                .ReadAsStringAsync(ct);
            var versionDirs  = JsonSerializer.Deserialize<GitHubItem[]>(
                versionsJson, JsonOptions);

            if (versionDirs is null || versionDirs.Length == 0)
            {
                _log.LogWarning(
                    "No version directories found for {Id}", wingetId);
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
                _log.LogWarning(
                    "Could not determine latest version for {Id}", wingetId);
                return null;
            }

            _log.LogInformation(
                "Latest version for {Id}: {Version}",
                wingetId, latestVersion.Raw);

            var installer = await FetchInstallerAsync(
                prefix, publisher, packageName, latestVersion.Raw, ct);

            if (installer is null)
            {
                _log.LogWarning(
                    "No suitable installer found for {Id} {Version}",
                    wingetId, latestVersion.Raw);
                return null;
            }

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
            _log.LogWarning(ex,
                "Failed to fetch winget manifest for {Id}", wingetId);
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

        _log.LogInformation(
            "Fetching installer manifest from {Url}", manifestUrl);

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
        return SelectBestInstaller(manifest.Installers ?? []);
    }

    private static WingetInstaller? SelectBestInstaller(
        List<WingetInstaller> installers)
    {
        return installers.FirstOrDefault(i =>
                i.Architecture.Equals("x64", StringComparison.OrdinalIgnoreCase) &&
                i.InstallerType.Equals("msi", StringComparison.OrdinalIgnoreCase))
            ?? installers.FirstOrDefault(i =>
                i.Architecture.Equals("x64", StringComparison.OrdinalIgnoreCase))
            ?? installers.FirstOrDefault(i =>
                i.InstallerType.Equals("msi", StringComparison.OrdinalIgnoreCase))
            ?? installers.FirstOrDefault();
    }

    private static WingetManifest ParseInstallerManifest(string yaml)
    {
        var manifest   = new WingetManifest();
        var installers = new List<WingetInstaller>();
        WingetInstaller? current = null;

        foreach (var rawLine in yaml.Split('\n'))
        {
            var trimmed = rawLine.TrimEnd().TrimStart();

            if (trimmed.StartsWith("- InstallerUrl:"))
            {
                current = new WingetInstaller();
                installers.Add(current);
            }

            if (current is null) continue;

            if (TryGetValue(trimmed, "InstallerUrl", out var url))
                current.InstallerUrl = url;
            else if (TryGetValue(trimmed, "InstallerSha256", out var hash))
                current.InstallerSha256 = hash;
            else if (TryGetValue(trimmed, "InstallerType", out var type))
                current.InstallerType = type;
            else if (TryGetValue(trimmed, "Architecture", out var arch))
                current.Architecture = arch;
            else if (TryGetValue(trimmed, "ProductCode", out var code))
                current.ProductCode = code;
            else if (TryGetValue(trimmed, "Silent", out var silent))
            {
                current.InstallerSwitches ??= new();
                current.InstallerSwitches.Silent = silent;
            }
        }

        manifest.Installers = installers;
        return manifest;
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