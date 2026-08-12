namespace Phylax.Connector.Models;

public class InstalledApp
{
    public string DisplayName { get; set; } = string.Empty;
    public string DisplayVersion { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string? ProductCode { get; set; }
    public string? WingetId { get; set; }
    public string InstallLocation { get; set; } = string.Empty;
    public DateTime? InstallDate { get; set; }
}
