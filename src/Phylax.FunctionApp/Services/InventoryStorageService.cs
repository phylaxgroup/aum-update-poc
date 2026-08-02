using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Phylax.FunctionApp.Models;

namespace Phylax.FunctionApp.Services;

public class InventoryStorageService
{
    private readonly ILogger<InventoryStorageService> _log;
    private readonly string _connectionString;
    private TableClient? _tableClient;
    private TableClient? _statusTableClient;
    private bool _initialized = false;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private const string TableName = "PhylaxInventory";
    private const string StatusTableName = "PhylaxConnectorStatus";

    public InventoryStorageService(ILogger<InventoryStorageService> log)
    {
        _log = log;

        _connectionString =
            Environment.GetEnvironmentVariable("PhylaxTableStorage")
            ?? Environment.GetEnvironmentVariable("AzureWebJobsStorage")
            ?? throw new InvalidOperationException(
                "PhylaxTableStorage connection string not configured.");
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            _tableClient = new TableClient(_connectionString, TableName);
            await _tableClient.CreateIfNotExistsAsync(ct);

            _statusTableClient = new TableClient(
                _connectionString, StatusTableName);
            await _statusTableClient.CreateIfNotExistsAsync(ct);

            _initialized = true;
            _log.LogInformation("Table Storage initialized successfully");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task UpsertInventoryAsync(
        string tenantId,
        string machineName,
        List<AppInventoryItem> apps,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        _log.LogInformation(
            "Upserting {Count} inventory records for {Machine} (tenant: {Tenant})",
            apps.Count, machineName, tenantId);

        var tasks = apps.Select(app =>
        {
            var record = new InventoryRecord
            {
                PartitionKey   = tenantId,
                RowKey         = $"{machineName}|{SanitizeKey(app.DisplayName)}",
                TenantId       = tenantId,
                MachineName    = machineName,
                DisplayName    = app.DisplayName,
                DisplayVersion = app.DisplayVersion,
                Publisher      = app.Publisher,
                ProductCode    = app.ProductCode,
                WingetId       = app.WingetId,
                LastSeen       = DateTimeOffset.UtcNow
            };

            return _tableClient!.UpsertEntityAsync(
                record, TableUpdateMode.Replace, ct);
        });

        await Task.WhenAll(tasks);
        _log.LogInformation("Inventory upsert complete for {Machine}", machineName);
    }

    public async Task<List<InventoryRecord>> GetTenantInventoryAsync(
        string tenantId,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var records = new List<InventoryRecord>();
        await foreach (var record in _tableClient!.QueryAsync<InventoryRecord>(
            filter: $"PartitionKey eq '{tenantId}'",
            cancellationToken: ct))
        {
            records.Add(record);
        }
        return records;
    }

    public async Task StoreStatusAsync(
        ConnectorStatusRequest status,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var entity = new TableEntity(
            status.TenantId,
            $"{status.MachineName}|{status.Timestamp:yyyyMMddHHmmss}")
        {
            ["MachineName"]      = status.MachineName,
            ["InventoryCount"]   = status.InventoryCount,
            ["UpdatesFound"]     = status.UpdatesFound,
            ["UpdateTitles"]     = string.Join(", ", status.UpdateTitles),
            ["ConnectorVersion"] = status.ConnectorVersion,
            ["Timestamp"]        = status.Timestamp
        };

        await _statusTableClient!.UpsertEntityAsync(
            entity, TableUpdateMode.Replace, ct);

        _log.LogDebug(
            "Status stored for {Machine} (tenant: {Tenant})",
            status.MachineName, status.TenantId);
    }

    private static string SanitizeKey(string value) =>
        new string(value
            .Replace('/', '_')
            .Replace('\\', '_')
            .Replace('#', '_')
            .Replace('?', '_')
            .Take(100)
            .ToArray());
}