using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Phylax.Connector.Services;
using Phylax.Connector.Models;

namespace Phylax.Connector;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _log;
    private readonly InventoryScanner _scanner;
    private readonly CatalogClient _catalog;
    private readonly LocalInstallerService _installer;
    private readonly IConfiguration _config;
    private readonly TimeSpan _checkInterval = TimeSpan.FromHours(6);

    public Worker(
        ILogger<Worker> log,
        InventoryScanner scanner,
        CatalogClient catalog,
        LocalInstallerService installer,
        IConfiguration config)
    {
        _log = log;
        _scanner = scanner;
        _catalog = catalog;
        _installer = installer;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // wsus            = publish SDPs to WSUS via the standalone net48 Phylax.WsusPublisher.exe;
        //                   do NOT install locally. Windows Update/AUM handles the actual install,
        //                   which is what makes updates visible in AUM compliance reporting. Run
        //                   this on ONE designated machine - publishing is a server-wide action.
        // winget          = install locally via winget. Fast, broad coverage, but invisible
        //                   to AUM update assessment.
        // inventory-only  = scan and report, take no action.
        var deliveryMode = (_config["PhylaxConnector:DeliveryMode"] ?? "winget").Trim().ToLowerInvariant();

        if (deliveryMode is not ("wsus" or "winget" or "inventory-only"))
        {
            _log.LogWarning("Unrecognized DeliveryMode '{Mode}' - falling back to 'winget'. " +
                "Valid values: wsus, winget, inventory-only.", deliveryMode);
            deliveryMode = "winget";
        }

        _log.LogInformation("Phylax Connector Service started on machine: {Machine} (delivery mode: {Mode})",
            Environment.MachineName, deliveryMode);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _log.LogInformation("--- Starting Phylax Update & Scanning Cycle ---");

                // 1. Scan local server ARP table registry baselines
                List<InstalledApp> inventory = _scanner.GetInstalledApplications();

                // 2. Diff local inventory against cloud Function App catalog definitions
                List<CatalogUpdate> availableUpdates = await _catalog.GetRequiredUpdatesAsync(inventory, stoppingToken);

                if (deliveryMode == "inventory-only")
                {
                    _log.LogInformation("Inventory-only mode: {Count} update(s) identified, taking no action.",
                        availableUpdates.Count);
                }
                else
                {
                    int processedCount = 0;
                    foreach (var update in availableUpdates)
                    {
                        _log.LogInformation("Processing required update: {App} -> v{Version} (KB: {KB})",
                            update.ApplicationName, update.NewVersion, update.KbArticleId);

                        bool success = deliveryMode == "wsus"
                            ? throw new NotImplementedException(
                                "WSUS delivery now runs through the standalone Phylax.WsusPublisher.exe - wiring pending. " +
                                "Test the publisher standalone first.")
                            : await _installer.InstallUpdateAsync(update, stoppingToken);

                        if (success)
                        {
                            processedCount++;
                        }
                    }

                    if (deliveryMode == "wsus")
                    {
                        _log.LogInformation("Cycle complete. Published {Processed}/{Total} package(s) to WSUS. " +
                            "Approve them in the WSUS console (or pass --approve) for clients to receive them.",
                            processedCount, availableUpdates.Count);
                    }
                    else
                    {
                        _log.LogInformation("Cycle complete. Successfully evaluated and patched {Processed}/{Total} applications on local endpoint.",
                            processedCount, availableUpdates.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "An unhandled exception occurred during the Phylax update cycle.");
            }

            // =====================================================================
            // NOTE FOR SCHEDULED TASKS / ONE-SHOT EXECUTION:
            // If running via Task Scheduler instead of a Windows Service, uncomment
            // the break statement below so the binary exits after one complete run:
            // break;
            // =====================================================================

            await Task.Delay(_checkInterval, stoppingToken);
        }
    }
}