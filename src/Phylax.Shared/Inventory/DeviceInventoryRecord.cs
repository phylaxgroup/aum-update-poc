using Azure;
using Azure.Data.Tables;

namespace Phylax.Shared.Inventory
{
    /// <summary>
    /// One row per machine that has ever called into Phylax.FunctionApp's catalog API. Read here
    /// by Phylax.Remediation's RemediationOrchestrator to resolve a real DeliveryTarget for the
    /// Defender-triggered path instead of guessing WsusManaged/HasVanguardAgent.
    ///
    /// This is a deliberate near-duplicate of Phylax.FunctionApp/Inventory/DeviceInventoryRecord.cs,
    /// not a shared reference from there - Phylax.FunctionApp does NOT take a ProjectReference on
    /// Phylax.Shared, because Phylax.Shared unconditionally references
    /// Microsoft.UpdateServices.Administration via a hardcoded local HintPath
    /// (C:\Program Files\Update Services\Api\...) across every target framework it builds. Pulling
    /// that in transitively would break Phylax.FunctionApp's build on the GitHub Actions
    /// windows-latest runner used by .github/workflows/main_func-phylax-poc.yml - that runner
    /// doesn't have the WSUS Administration console installed, and FunctionApp is the only project
    /// that workflow actually builds today. Phylax.Remediation already depends on Phylax.Shared (via
    /// WsusDeliveryAdapter) so adding this here carries no new risk for it. If Phylax.Shared is ever
    /// split so its non-WSUS pieces (Models/Catalog/Inventory) don't drag in the WSUS reference,
    /// these two copies should be collapsed back into one.
    /// </summary>
    public class DeviceInventoryRecord : ITableEntity
    {
        public string PartitionKey { get; set; } = "device";
        public string RowKey { get; set; } = string.Empty;
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }

        public string MachineName { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;
        public bool HasVanguardAgent { get; set; }
        public bool WsusManaged { get; set; }
        public DateTimeOffset LastSeenUtc { get; set; }
    }
}
