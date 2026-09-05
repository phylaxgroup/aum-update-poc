using Azure;
using Azure.Data.Tables;

namespace Phylax.FunctionApp.Inventory;

/// <summary>
/// One row per machine that has ever called into the catalog API. Written by
/// <see cref="Functions.CatalogUpdatesFunction"/> on every scan-cycle request, read by
/// Phylax.Remediation's RemediationOrchestrator (its own copy of this type, in
/// Phylax.Shared/Inventory - see the note there for why it isn't shared code) so the
/// Defender-triggered path can tell WSUS-managed boxes from everything else instead of
/// guessing.
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
