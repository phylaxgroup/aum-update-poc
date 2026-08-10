using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Phylax.FunctionApp.Services;

public class WingetManifestService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WingetManifestService> _log;

    public WingetManifestService(
        IHttpClientFactory httpClientFactory,
        ILogger<WingetManifestService> log)
    {
        _httpClientFactory = httpClientFactory;
        _log               = log;
    }

    public async Task<string?> GetLatestVersionAsync(
        string wingetId, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient("winget");
        
        // Dynamically query the public WinGet manifest repository structure via GitHub API
        var parts = wingetId.Split('.');
        if (parts.Length < 2) return null;

        var publisher = parts[0];
        var appName = parts[1];
        var treeUrl = $"https://api.github.com/repos/microsoft/winget-pkgs/contents/manifests/{publisher[0].ToString().ToLowerInvariant()}/{publisher}/{appName}";

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
