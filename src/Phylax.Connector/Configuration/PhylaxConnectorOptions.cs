namespace Phylax.Connector.Configuration;

public class PhylaxConnectorOptions
{
    public string TenantId { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public int ScanIntervalHours { get; set; } = 24;
    public string VendorName { get; set; } = "Phylax";
    public string ProductName { get; set; } = "Phylax Third-Party Updates";
    public string PayloadStagingPath { get; set; } = @"C:\ProgramData\Phylax\Staging";
    public string SdpOutputPath { get; set; } = @"C:\ProgramData\Phylax\SDP";
}
