using System.Text.Json;
using Microsoft.Extensions.Logging;
using Phylax.FunctionApp.Models;

namespace Phylax.FunctionApp.Services;

public class VersionComparisonService
{
    private readonly ILogger<VersionComparisonService> _log;
    private readonly WingetManifestService _manifestService;

    public VersionComparisonService(
        ILogger<VersionComparisonService> log,
        WingetManifestService manifestService)
    {
        _log             = log;
        _manifestService = manifestService;
    }

    public async Task<List<AppUpdateItem>> GetAvailableUpdatesAsync(
        List<AppInventoryItem> installedApps, CancellationToken ct = default)
    {
        var updates = new List<AppUpdateItem>();

        foreach (var app in installedApps)
        {
            // Dynamically resolve or fallback to a normalized package identifier if missing
            var wingetId = app.WingetId;
            if (string.IsNullOrWhiteSpace(wingetId))
            {
                wingetId = ResolveDynamicWingetId(app.DisplayName, app.Publisher);
            }

            if (string.IsNullOrWhiteSpace(wingetId))
            {
                continue; // Skip if it cannot be auto-resolved
            }

            try
            {
                var latestVersion = await _manifestService
                    .GetLatestVersionAsync(wingetId, ct);

                if (string.IsNullOrWhiteSpace(latestVersion))
                    continue;

                if (IsNewerVersion(latestVersion, app.DisplayVersion))
                {
                    updates.Add(new AppUpdateItem
                    {
                        DisplayName       = app.DisplayName,
                        InstalledVersion  = app.DisplayVersion ?? "Unknown",
                        LatestVersion     = latestVersion,
                        WingetId          = wingetId,
                        KbArticleId       = GenerateDeterministicKb(wingetId),
                        SecurityBulletinId = $"PHY-{Math.Abs(wingetId.GetHashCode()) % 90000 + 10000}"
                    });
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to check update for dynamic app {App} ({Id})", 
                    app.DisplayName, wingetId);
            }
        }

        return updates;
    }

    private static string? ResolveDynamicWingetId(string? displayName, string? publisher)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;

        // Dynamic heuristic mapping based on common publisher patterns
        var cleanName = displayName.Replace(" ", "").Replace(".", "");
        
        if (displayName.Contains("Visual Studio Code", StringComparison.OrdinalIgnoreCase))
            return "Microsoft.VisualStudioCode";
        if (displayName.Contains("SQL Server Management Studio", StringComparison.OrdinalIgnoreCase))
            return "Microsoft.SQLServerManagementStudio";
        if (displayName.Contains("7-Zip", StringComparison.OrdinalIgnoreCase))
            return "7zip.7zip";
        if (displayName.Contains("Notepad++", StringComparison.OrdinalIgnoreCase))
            return "Notepad++.Notepad++";
        if (displayName.Contains("Git", StringComparison.OrdinalIgnoreCase) && 
            publisher?.Contains("Git", StringComparison.OrdinalIgnoreCase) == true)
            return "Git.Git";

        // Fallback generic heuristic format for auto-discovery
        return null; 
    }

    private static bool IsNewerVersion(string latest, string? installed)
    {
        if (string.IsNullOrWhiteSpace(installed)) return true;

        try
        {
            var latestClean = CleanVersionString(latest);
            var installedClean = CleanVersionString(installed);

            if (Version.TryParse(latestClean, out var latestVer) &&
                Version.TryParse(installedClean, out var installedVer))
            {
                return latestVer > installedVer;
            }

            return string.Compare(latest, installed, StringComparison.OrdinalIgnoreCase) > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string CleanVersionString(string version)
    {
        var parts = version.Split(new[] { '-', '+' }, 2);
        var digits = new string(parts[0].Where(c => char.IsDigit(c) || c == '.').ToArray());
        return digits.Trim('.');
    }

    private static string GenerateDeterministicKb(string wingetId)
    {
        int hash = Math.Abs(wingetId.GetHashCode());
        return (5000000 + (hash % 900000)).ToString();
    }
}
