using Azure;
using Azure.Data.Tables;

namespace Phylax.FunctionApp.Inventory;

/// <summary>
/// One row per machine that has ever called into the catalog API. Written by
/// <see cref="Functions.CatalogUpdatesFunction"/> on every scan-cycle request, giving a
/// fleet-wide view of which machines are reporting in and how they are configured.
///
/// Originally written to be read back by the Defender-driven RemediationOrchestrator, which
/// kept a near-identical copy of this type in Phylax.Shared/Inventory. That generation and its
/// duplicate type were removed 2026-09-11, leaving this as the only copy. Nothing reads these
/// rows back today.
/// </summary>
public class DeviceInventoryRecord : ITableEntity
{
    public string PartitionKey { get; set; } = "device";
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string MachineName { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;

    /// <summary>Always true when written here - only machines with the connector installed call this API.</summary>
    public bool HasVanguardAgent { get; set; }

    /// <summary>From the connector's own reported DeliveryMode (see CatalogUpdateRequest.DeliveryMode) - not guessed.</summary>
    public bool WsusManaged { get; set; }

    public DateTimeOffset LastSeenUtc { get; set; }
}
