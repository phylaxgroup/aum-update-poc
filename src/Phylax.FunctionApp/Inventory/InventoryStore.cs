using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace Phylax.FunctionApp.Inventory;

/// <summary>
/// Thin Azure Table Storage wrapper for device inventory records. Takes a connection string
/// (not a TableServiceClient) so callers don't need a direct Azure.Data.Tables package
/// reference just to register this in DI - only this project does.
///
/// Reuses the AzureWebJobsStorage connection string every Azure Functions app already has,
/// so there's no new secret to provision for this POC.
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

    public async Task UpsertAsync(
        string machineName, string domain, bool wsusManaged, CancellationToken ct = default)
    {
        var record = new DeviceInventoryRecord
        {
            RowKey = NormalizeMachineName(machineName),
            MachineName = machineName,
            Domain = domain,
            HasVanguardAgent = true,
            WsusManaged = wsusManaged,
            LastSeenUtc = DateTimeOffset.UtcNow
        };

        await _table.UpsertEntityAsync(record, TableUpdateMode.Replace, ct);
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
    /// Matches on short hostname, case-insensitive, so "PTG-WIN25" (Environment.MachineName,
    /// what the connector reports) and "ptg-win25.contoso.local" (ComputerDnsName, what
    /// Defender for Endpoint reports) resolve to the same row.
    /// </summary>
    private static string NormalizeMachineName(string name) =>
        name.Split('.')[0].Trim().ToUpperInvariant();
}
