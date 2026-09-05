using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Phylax.FunctionApp.Services;

public class WingetManifestService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<WingetManifestService> _log;

    // Unauthenticated GitHub API calls are capped at 60/hr per caller IP. A fleet of connectors
    // all reporting in on the same scan cycle would otherwise fan out one GitHub call per app
    // per machine - this cache keeps repeat lookups for the same WingetId within the window free.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(60);

    public WingetManifestService(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<WingetManifestService> log)
    {
        _httpClientFactory = httpClientFactory;
        _cache             = cache;
        _log               = log;
    }

    public async Task<string?> GetLatestVersionAsync(
        string wingetId, CancellationToken ct = default)
    {
        var cacheKey = $"winget-latest:{wingetId}";
        if (_cache.TryGetValue(cacheKey, out string? cached))
        {
            return cached;
        }

        var latest = await FetchLatestVersionAsync(wingetId, ct);

        // Cache misses (null) too, at a shorter TTL, so a manifest lookup that's failing
        // (rate-limited, id not found) doesn't get retried on every single request in the
        // window - but also doesn't stay "stuck" failing for a full hour once it recovers.
        _cache.Set(cacheKey, latest, latest is null ? TimeSpan.FromMinutes(5) : CacheTtl);
        return latest;
    }

    private async Task<string?> FetchLatestVersionAsync(string wingetId, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("winget");

        // Dynamically query the public WinGet manifest repository structure via GitHub API.
        // WinGet IDs can have more than 2 segments (e.g. "Adobe.Acrobat.Reader.64-bit" ->
        // manifests/a/Adobe/Acrobat/Reader/64-bit) - only the first segment is the publisher
        // folder, everything after it is nested subfolders, so join the rest as path segments
        // rather than assuming exactly two.
        var parts = wingetId.Split('.');
        if (parts.Length < 2) return null;

        var publisher = parts[0];
        var appPath = string.Join('/', parts.Skip(1));
        var treeUrl = $"https://api.github.com/repos/microsoft/winget-pkgs/contents/manifests/{publisher[0].ToString().ToLowerInvariant()}/{publisher}/{appPath}";

        try
        {
            var response = await client.GetAsync(treeUrl, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogDebug("Manifest path not found for {Id} at {Url}", wingetId, treeUrl);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(content);
            
            var versions = new List<string>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (element.TryGetProperty("name", out var nameProp))
                    {
                        var version = nameProp.GetString();
                        if (!string.IsNullOrEmpty(version))
                        {
                            versions.Add(version);
                        }
                    }
                }
            }

            // Sort versions using semantic comparison to get the latest
            var latest = versions
                .OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            return latest;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Error fetching manifest for {Id}", wingetId);
            return null;
        }
    }
}
