using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Ingestion;
using Microsoft.Extensions.Logging;
using Phylax.FunctionApp.Models;

namespace Phylax.FunctionApp.Services;

public class LogAnalyticsIngestionService
{
    private readonly ILogger<LogAnalyticsIngestionService> _log;
    private readonly LogsIngestionClient? _client;
    private readonly string? _ruleId;
    private readonly string? _streamName;
    private readonly bool _isConfigured = false;

    public LogAnalyticsIngestionService(ILogger<LogAnalyticsIngestionService> log)
    {
        _log = log;

        var endpointUri = Environment.GetEnvironmentVariable("DataCollectionEndpoint");
        _ruleId = Environment.GetEnvironmentVariable("DataCollectionRuleId");
        _streamName = Environment.GetEnvironmentVariable("StreamName");

        if (!string.IsNullOrEmpty(endpointUri) && !string.IsNullOrEmpty(_ruleId) && !string.IsNullOrEmpty(_streamName))
        {
            var endpoint = new Uri(endpointUri);
            _client = new LogsIngestionClient(endpoint, new DefaultAzureCredential());
            _isConfigured = true;
        }
        else
        {
            _log.LogWarning("LogAnalyticsIngestionService is missing required configuration environment variables.");
        }
    }

    public async Task UploadInventoryAsync(string tenantId, string machineName, List<AppInventoryItem> apps, CancellationToken ct = default)
    {
        if (!_isConfigured || _client == null)
        {
            _log.LogError("Ingestion client is unconfigured. Skipping upload.");
            return;
        }

        var telemetryPayload = apps.Select(app => new
        {
            TenantId = tenantId,
            MachineName = machineName,
            DisplayName = app.DisplayName,
            DisplayVersion = app.DisplayVersion,
            Publisher = app.Publisher,
            ProductCode = app.ProductCode,
            WingetId = app.WingetId,
            TimeGenerated = DateTimeOffset.UtcNow
        }).ToList();

        try
        {
            _log.LogInformation("Uploading {Count} records for {Machine} to Log Analytics...", telemetryPayload.Count, machineName);
            
            var jsonPayload = JsonSerializer.Serialize(telemetryPayload);
            var requestContent = RequestContent.Create(jsonPayload);
            
            var response = await _client.UploadAsync(_ruleId!, _streamName!, requestContent, cancellationToken: ct);

            if (response.IsError)
            {
                _log.LogError("Log Analytics upload failed with status code: {Status}", response.Status);
            }
            else
            {
                _log.LogInformation("Inventory telemetry successfully pushed to workspace for {Machine}.", machineName);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unhandled error during log analytics ingestion for machine {Machine}", machineName);
        }
    }
}
