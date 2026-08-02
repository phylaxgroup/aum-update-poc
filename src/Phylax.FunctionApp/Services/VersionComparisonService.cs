using Microsoft.Extensions.Logging;
using Phylax.FunctionApp.Models;

namespace Phylax.FunctionApp.Services;

/// <summary>
/// Compares installed versions against latest winget manifest versions
/// and produces a list of available updates.
/// </summary>
public class VersionComparisonService
{
    private readonly ILogger<VersionComparisonService> _log;
    private readonly WingetManifestService _winget;

    public VersionComparisonService(
        ILogger<VersionComparisonService> log,
        WingetManifestService winget)
    {
        _log    = log;
        _winget = winget;
    }

    /// <summary>
    /// For each app in the inventory, checks winget for a newer version.
    /// Returns only apps that have updates available.
    /// </summary>
    public async Task<List<AvailableUpdate>> GetAvailableUpdatesAsync(
        List<AppInventoryItem> inventory,
        CancellationToken ct = default)
    {
        var updates = new List<AvailableUpdate>();

        // Process in parallel with a concurrency limit
        // to avoid hammering GitHub API
        var semaphore = new SemaphoreSlim(5); // max 5 concurrent requests
        var tasks     = inventory
            .Where(app => !string.IsNullOrWhiteSpace(app.WingetId))
            .Select(async app =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    return await CheckForUpdateAsync(app, ct);
                }
                finally
                {
                    semaphore.Release();
                }
            });

        var results = await Task.WhenAll(tasks);
        updates.AddRange(results.Where(u => u is not null)!);

        // Also attempt server-side winget ID matching for apps
        // the connector couldn't resolve locally
        var unmatched = inventory
            .Where(app => string.IsNullOrWhiteSpace(app.WingetId))
            .ToList();

        if (unmatched.Count > 0)
        {
            _log.LogInformation(
                "{Count} apps without winget IDs — skipping " +
                "(server-side matching not yet implemented in V1)",
                unmatched.Count);
        }

        _log.LogInformation(
            "Version comparison complete: {Total} apps checked, " +
            "{Updates} updates available",
            inventory.Count, updates.Count);

        return updates;
    }

    private async Task<AvailableUpdate?> CheckForUpdateAsync(
        AppInventoryItem app,
        CancellationToken ct)
    {
        try
        {
            var latest = await _winget.GetLatestVersionAsync(app.WingetId!, ct);
            if (latest is null) return null;

            var installedVersion = TryParseVersion(app.DisplayVersion);
            var latestVersion    = TryParseVersion(latest.LatestVersion);

            if (installedVersion is null || latestVersion is null)
            {
                // Fall back to string comparison if version parsing fails
                if (string.Equals(app.DisplayVersion, latest.LatestVersion,
                    StringComparison.OrdinalIgnoreCase))
                    return null;
            }
            else if (installedVersion >= latestVersion)
            {
                _log.LogDebug(
                    "{App} is current: {Installed} >= {Latest}",
                    app.DisplayName, app.DisplayVersion, latest.LatestVersion);
                return null;
            }

            return new AvailableUpdate
            {
                ApplicationName   = app.DisplayName,
                WingetId          = app.WingetId!,
                CurrentVersion    = app.DisplayVersion,
                NewVersion        = latest.LatestVersion,
                InstallerUrl      = latest.InstallerUrl,
                InstallerType     = latest.InstallerType,
                ProductCode       = latest.ProductCode ?? app.ProductCode ?? string.Empty,
                SilentInstallArgs = latest.SilentArgs,
                Sha256Hash        = latest.InstallerSha256,
                RebootRequired    = false
            };
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex,
                "Version check failed for {App}", app.DisplayName);
            return null;
        }
    }

    private static Version? TryParseVersion(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var cleaned = v.TrimStart('v', 'V');
        if (!cleaned.Contains('.')) cleaned += ".0";
        return Version.TryParse(cleaned, out var parsed) ? parsed : null;
    }
}