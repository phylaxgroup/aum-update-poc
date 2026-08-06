using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Phylax.Connector.Models;

namespace Phylax.Connector.Services;

public class InventoryScanner
{
    private readonly ILogger<InventoryScanner> _log;

    private static readonly string[] RegistryKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    ];

    public InventoryScanner(ILogger<InventoryScanner> log)
    {
        _log = log;
    }

    public List<InstalledApp> ScanLocalMachine()
    {
        var apps = new List<InstalledApp>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hiveKeyPath in RegistryKeys)
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var subKey = baseKey.OpenSubKey(hiveKeyPath);
            if (subKey is null) continue;

            foreach (var subKeyName in subKey.GetSubKeyNames())
            {
                using var appKey = subKey.OpenSubKey(subKeyName);
                if (appKey is null) continue;

                var displayName = appKey.GetValue("DisplayName") as string;
                var displayVersion = appKey.GetValue("DisplayVersion") as string;
                var publisher = appKey.GetValue("Publisher") as string ?? "Unknown";

                // Skip system components, KBs, or entries without version strings
                if (string.IsNullOrWhiteSpace(displayName) ||
                    string.IsNullOrWhiteSpace(displayVersion) ||
                    IsSystemComponent(appKey))
                {
                    continue;
                }

                // Deduplicate if an app registered in both 32-bit and 64-bit hives
                var dedupKey = $"{displayName}|{displayVersion}";
                if (!seenKeys.Add(dedupKey)) continue;

                // --- RESOLVE OFFICIAL WINGET ID ---
                var wingetId = KnownAppCatalog.TryResolveWingetId(displayName, publisher);

                if (wingetId != null)
                {
                    _log.LogDebug("Mapped '{App}' ({Version}) -> Winget ID: {WingetId}",
                        displayName, displayVersion, wingetId);
                }

                apps.Add(new InstalledApp(
                    DisplayName: displayName,
                    DisplayVersion: displayVersion,
                    Publisher: publisher,
                    ProductCode: subKeyName,
                    WingetId: wingetId
                ));
            }
        }

        var mappedCount = apps.Count(a => !string.IsNullOrWhiteSpace(a.WingetId));
        _log.LogInformation("Inventory scan complete. Discovered {Total} applications ({Mapped} mapped to WinGet catalog).",
            apps.Count, mappedCount);

        return apps;
    }

    private static bool IsSystemComponent(RegistryKey appKey)
    {
        var systemComponent = appKey.GetValue("SystemComponent");
        if (systemComponent is int intVal && intVal == 1) return true;
        
        var parentKeyName = appKey.GetValue("ParentKeyName") as string;
        return !string.IsNullOrWhiteSpace(parentKeyName);
    }
}
