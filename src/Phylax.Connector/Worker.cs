using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phylax.Connector.Configuration;
using Phylax.Connector.Services;

namespace Phylax.Connector;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _log;
    private readonly InventoryScanner _scanner;
    private readonly CatalogClient _catalog;
    private readonly WsusPublisher _publisher;
    private readonly TimeSpan _checkInterval = TimeSpan.FromHours(6);

    public Worker(
        ILogger<Worker> log,
        InventoryScanner scanner,
        CatalogClient catalog,
        WsusPublisher publisher)
    {
        _log = log;
        _scanner = scanner;
        _catalog = catalog;
        _publisher = publisher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Phylax Connector Service started on machine: {Machine}", Environment.MachineName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _log.LogInformation("--- Starting Phylax Update & Publishing Cycle ---");

                // 1. Scan local machine inventory (enriched with WinGet IDs)
                var inventory = _scanner.ScanLocalMachine();

                // 2. Request matching security updates from Function App
                var availableUpdates = await _catalog.GetRequiredUpdatesAsync(inventory, stoppingToken);

                // 3. Publish each new update to WSUS with v11 Security classification
                int publishedCount = 0;
                foreach (var update in availableUpdates)
                {
                    _log.LogInformation("Processing update: {App} -> v{Version} (KB: {KB})", 
                        update.ApplicationName, update.NewVersion, update.KbArticleId);

                    bool success = await _publisher.PublishUpdateAsync(update, stoppingToken);
                    if (success)
                    {
                        publishedCount++;
                    }
                }

                _log.LogInformation("Cycle complete. Successfully published {Published}/{Total} updates to WSUS.", 
                    publishedCount, availableUpdates.Count);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "An unhandled exception occurred during the Phylax update cycle.");
            }

            _log.LogInformation("Sleeping for {Hours} hours until next scan cycle...", _checkInterval.TotalHours);
            await Task.Delay(_checkInterval, stoppingToken);
        }
    }
}
