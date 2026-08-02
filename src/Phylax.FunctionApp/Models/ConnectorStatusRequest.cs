namespace Phylax.FunctionApp.Models;

public class ConnectorStatusRequest
{
    public string TenantId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public int InventoryCount { get; set; }
    public int UpdatesFound { get; set; }
    public List<string> UpdateTitles { get; set; } = [];
    public string ConnectorVersion { get; set; } = string.Empty;
}