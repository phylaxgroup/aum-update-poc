using Azure;
using Azure.Data.Tables;

namespace Phylax.Shared.Inventory
{
    /// <summary>
    /// Read-side twin of Phylax.FunctionApp/Inventory/InventoryStore.cs - see
    /// DeviceInventoryRecord.cs in this folder for why this is a separate copy rather than a
    /// shared reference. Points at the same "PhylaxDeviceInventory" table via the same
    /// AzureWebJobsStorage connection string, so both Function Apps read/write the same data.
    /// </summary>
    public class InventoryStore
    {
        private const string TableName = "PhylaxDeviceInventory";
        private const string PartitionKey = "device";

        private readonly TableClient _table;

        public InventoryStore(string storageConnectionString)
        {
            var serviceClient = new TableServiceClient(storageConnectionString);
            _table = serviceClient.GetTableClient(TableName);
            _table.CreateIfNotExists();
        }

        public async Task<DeviceInventoryRecord?> TryGetAsync(
            string machineNameOrDnsName, CancellationToken ct = default)
        {
            try
            {
                var response = await _table.GetEntityAsync<DeviceInventoryRecord>(
                    PartitionKey, NormalizeMachineName(machineNameOrDnsName), cancellationToken: ct);
                return response.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        /// <summary>
        /// Matches on short hostname, case-insensitive, so "PTG-WIN25" (what the connector
        /// reports via Environment.MachineName) and "ptg-win25.contoso.local" (Defender for
        /// Endpoint's ComputerDnsName) resolve to the same row.
        /// </summary>
        private static string NormalizeMachineName(string name) =>
            name.Split('.')[0].Trim().ToUpperInvariant();
    }
}
