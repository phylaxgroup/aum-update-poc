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
    private readonly TimeSpan _checkInterval = TimeSpan.FromHours(6);

    public Worker(
        ILogger<Worker> log,
        InventoryScanner scanner,
        CatalogClient catalog,
        LocalInstallerService installer) 
    {
        _log = log;
        _scanner = scanner;
        _catalog = catalog;
        _installer = installer;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Phylax Connector Service started on machine: {Machine}", Environment.MachineName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _log.LogInformation("--- Starting Phylax Update & Scanning Cycle ---");
                
                // 1. Scan local server ARP table registry baselines
                List<InstalledApp> inventory = _scanner.ScanLocalMachine();
                
                // 2. Diff local inventory against cloud Function App catalog definitions
                List<AvailableUpdate> availableUpdates = await _catalog.GetRequiredUpdatesAsync(inventory, stoppingToken);

                int processedCount = 0;
                foreach (var update in availableUpdates)
                {
                    _log.LogInformation("Processing required update: {App} -> v{Version} (KB: {KB})", 
                        update.ApplicationName, update.NewVersion, update.KbArticleId);

                    // 3. Execute local silent installation engine
                    bool success = await _installer.InstallUpdateAsync(update.ApplicationName, update.NewVersion, stoppingToken);
                    
                    if (success)
                    {
                        processedCount++;
                    }
                }

                _log.LogInformation("Cycle complete. Successfully evaluated and patched {Processed}/{Total} applications on local endpoint.", 
                    processedCount, availableUpdates.Count);
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
