using Azure;
using Azure.Data.Tables;

namespace Phylax.FunctionApp.Models;

public class InventoryRecord : ITableEntity
{
    // PartitionKey = TenantId, RowKey = MachineName+AppName
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string TenantId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DisplayVersion { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string? ProductCode { get; set; }
    public string? WingetId { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}