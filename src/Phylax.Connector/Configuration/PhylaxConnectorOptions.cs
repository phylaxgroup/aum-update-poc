namespace Phylax.Connector.Configuration;

public class PhylaxConnectorOptions
{
    public string TenantId { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string EvaluateUpdatesEndpoint { get; set; } = "api/catalog/updates";
    public string ApiKey { get; set; } = string.Empty;
    public int PollingIntervalHours { get; set; } = 6;
}
