using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Phylax.FunctionApp.Inventory;
using Phylax.FunctionApp.Models;
using Phylax.FunctionApp.Services;

namespace Phylax.FunctionApp.Functions;

public class CatalogUpdatesFunction
{
    private readonly ILogger<CatalogUpdatesFunction> _logger;
    private readonly VersionComparisonService _versionService;
    private readonly WingetManifestService _wingetManifestService;
    private readonly InventoryStore _inventoryStore;

    public CatalogUpdatesFunction(
        ILogger<CatalogUpdatesFunction> logger,
        VersionComparisonService versionService,
        WingetManifestService wingetManifestService,
        InventoryStore inventoryStore)
    {
        _logger = logger;
        _versionService = versionService;
        _wingetManifestService = wingetManifestService;
        _inventoryStore = inventoryStore;
    }

    // A structured Master Catalog definition representing what Phylax supports
    private static readonly List<MasterCatalogItem> MasterCatalog = new()
    {
        new MasterCatalogItem
        {
            ApplicationName = "7-Zip",
            WingetId = "7zip.7zip",
            LatestVersion = "24.08",
            InstallerUrl = "https://www.7-zip.org/a/7z2408-x64.msi",
            InstallerType = "msi",
            SilentInstallArgs = "/quiet /norestart",
            SecurityBulletinId = "MS26-PHY11",
            KbArticleId = "5000011"
        },
        new MasterCatalogItem
        {
            ApplicationName = "Notepad++",
            WingetId = "Notepad++.Notepad++",
            LatestVersion = "8.6.9",
            InstallerUrl = "https://github.com/notepad-plus-plus/notepad-plus-plus/releases/download/v8.6.9/npp.8.6.9.Installer.x64.exe",
            InstallerType = "exe",
            SilentInstallArgs = "/S",
            SecurityBulletinId = "MS26-PHY12",
            KbArticleId = "5000012"
        },
        new MasterCatalogItem
        {
            ApplicationName = "Google Chrome",
            WingetId = "Google.Chrome",
            LatestVersion = "127.0.6533.120",
            InstallerUrl = "https://dl.google.com/dl/chrome/install/googlechromestandaloneenterprise64.msi",
            InstallerType = "msi",
            SilentInstallArgs = "/qn /norestart",
            SecurityBulletinId = "MS26-PHY13",
            KbArticleId = "5000013"
        },
        new MasterCatalogItem
        {
            // Verified 2026-08-15: 0.84 released 2026-05-22. Versioned URL, so no evergreen
            // drift risk - when 0.85 ships, both LatestVersion and InstallerUrl need updating.
            ApplicationName = "PuTTY",
            WingetId = "PuTTY.PuTTY",
            LatestVersion = "0.84",
            InstallerUrl = "https://the.earth.li/~sgtatham/putty/latest/w64/putty-64bit-0.84-installer.msi",
            InstallerType = "msi",
            SilentInstallArgs = "/qn /norestart",
            SecurityBulletinId = "MS26-PHY14",
            KbArticleId = "5000014"
        }
    };

    [Function("CatalogUpdates")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "catalog/updates")] HttpRequest req,
        CancellationToken ct)
    {
        _logger.LogInformation("Processing dynamic multi-package update evaluation request.");

        string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
        var requestData = JsonSerializer.Deserialize<CatalogUpdateRequest>(requestBody, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        // Record/refresh this machine's inventory row on every call-in, regardless of whether it
        // has updates pending, so there is a fleet-wide view of which machines are reporting in
        // and how they are configured. WsusManaged comes from the connector's own reported
        // DeliveryMode rather than being assumed - an empty or unrecognized DeliveryMode is
        // treated as "not WSUS-managed" here.
        //
        // Originally added to let the Defender-driven RemediationOrchestrator resolve a real
        // DeliveryTarget; that generation was removed 2026-09-11. The row is retained because
        // fleet visibility is useful on its own, but nothing currently reads it back.
        if (!string.IsNullOrWhiteSpace(requestData?.MachineName))
        {
            bool wsusManaged = string.Equals(requestData.DeliveryMode, "wsus", StringComparison.OrdinalIgnoreCase);
            await _inventoryStore.UpsertAsync(requestData.MachineName, requestData.Domain, wsusManaged, ct);
        }

        var availableUpdates = new List<CatalogUpdate>();

        if (requestData?.InstalledApplications == null || requestData.InstalledApplications.Count == 0)
        {
            // Previously this returned the ENTIRE master catalog with CurrentVersion "0.0.0",
            // which made sense when the only consumer was WSUS metadata seeding. It is actively
            // dangerous now that LocalInstallerService acts on the same response: an empty
            // inventory caused the connector to attempt installing software that was never on
            // the machine (observed on ptg-win25-client 2026-08-15).
            //
            // Vanguard patches what is present. It does not install what isn't.
            _logger.LogWarning(
                "Inventory payload from {Machine} was empty or missing - returning no updates. " +
                "If this machine genuinely has third-party software installed, check the connector's " +
                "InventoryScanner exclusion filters or that it is running with sufficient registry access.",
                requestData?.MachineName ?? "(unknown)");
        }
        else
        {
            _logger.LogInformation("Evaluating {Count} submitted applications from {Machine} against master catalog.",
                requestData.InstalledApplications.Count, requestData.MachineName);

            foreach (var masterItem in MasterCatalog)
            {
                var matchedClientApp = requestData.InstalledApplications.FirstOrDefault(a =>
                    a.DisplayName.Contains(masterItem.ApplicationName, StringComparison.OrdinalIgnoreCase));

                if (matchedClientApp != null)
                {
                    if (_versionService.IsUpdateAvailable(matchedClientApp.DisplayVersion, masterItem.LatestVersion))
                    {
                        _logger.LogInformation("Outdated software flagged: {App} (Client: {CVer} -> Latest: {LVer})",
                            masterItem.ApplicationName, matchedClientApp.DisplayVersion, masterItem.LatestVersion);

                        availableUpdates.Add(MapToCatalogUpdate(masterItem, matchedClientApp.DisplayVersion, matchedClientApp.WingetId));
                    }

                    // Live catalog-drift check, logging only. MasterCatalog's InstallerUrl is a
                    // version-baked static URL (e.g. "7z2408-x64.msi" literally encodes 24.08) -
                    // we deliberately do NOT swap in the live winget version as NewVersion here,
                    // since that would tell the client "install v25.01" while the InstallerUrl
                    // still points at v24.08's file. This just tells a human the catalog entry
                    // needs a manual bump (version + URL together) once winget has moved on.
                    var liveVersion = await _wingetManifestService.GetLatestVersionAsync(masterItem.WingetId, ct);
                    if (liveVersion != null &&
                        _versionService.IsUpdateAvailable(masterItem.LatestVersion, liveVersion))
                    {
                        _logger.LogWarning(
                            "Catalog drift: {App} MasterCatalog is pinned to {Static}, but WinGet's live " +
                            "manifest shows {Live} as latest. Update MasterCatalog's LatestVersion + " +
                            "InstallerUrl together before clients start missing this update.",
                            masterItem.ApplicationName, masterItem.LatestVersion, liveVersion);
                    }
                }
            }

            // Visibility for apps the connector found but this catalog doesn't cover. Without
            // this, an unsupported app is indistinguishable from an up-to-date one in the logs.
            var uncoveredCount = requestData.InstalledApplications.Count(clientApp =>
                !MasterCatalog.Any(m => clientApp.DisplayName.Contains(m.ApplicationName, StringComparison.OrdinalIgnoreCase)));

            if (uncoveredCount > 0)
            {
                _logger.LogInformation("{Count} installed applications are not covered by the master catalog (no update evaluation performed for these).",
                    uncoveredCount);
            }
        }

        return new OkObjectResult(new CatalogUpdateResponse { Updates = availableUpdates });
    }

    private static CatalogUpdate MapToCatalogUpdate(MasterCatalogItem item, string currentVersion, string? clientWingetId = null)
    {
        return new CatalogUpdate
        {
            ApplicationName = item.ApplicationName,
            CurrentVersion = currentVersion,
            NewVersion = item.LatestVersion,
            InstallerUrl = item.InstallerUrl,
            InstallerType = item.InstallerType,
            // Prefer the WingetId the connector resolved locally (via KnownAppCatalog or the
            // winget CLI); fall back to the catalog's own value if the client didn't resolve one.
            WingetId = string.IsNullOrWhiteSpace(clientWingetId) ? item.WingetId : clientWingetId,
            SilentInstallArgs = item.SilentInstallArgs,
            Sha256Hash = "",
            SecurityBulletinId = item.SecurityBulletinId,
            KbArticleId = item.KbArticleId
        };
    }

    private class MasterCatalogItem
    {
        public string ApplicationName { get; set; } = string.Empty;
        public string WingetId { get; set; } = string.Empty;
        public string LatestVersion { get; set; } = string.Empty;
        public string InstallerUrl { get; set; } = string.Empty;
        public string InstallerType { get; set; } = "msi";
        public string SilentInstallArgs { get; set; } = string.Empty;
        public string SecurityBulletinId { get; set; } = string.Empty;
        public string KbArticleId { get; set; } = string.Empty;
    }
}
