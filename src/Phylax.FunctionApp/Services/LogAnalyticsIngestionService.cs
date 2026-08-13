using Microsoft.Extensions.Logging;
using Phylax.FunctionApp.Models;

namespace Phylax.FunctionApp.Services;

public class LogAnalyticsIngestionService
{
    private readonly ILogger<LogAnalyticsIngestionService> _logger;

    public LogAnalyticsIngestionService(ILogger<LogAnalyticsIngestionService> logger)
    {
        _logger = logger;
    }

    public async Task ProcessIngestionAsync(string machineName, List<InstalledApp> applications)
    {
        _logger.LogInformation("Processing workspace telemetry ingestion for {Machine} with {Count} items.", 
            machineName, applications.Count);

        // Telemetry pass-through pipeline stub
        await Task.CompletedTask;
    }
}
