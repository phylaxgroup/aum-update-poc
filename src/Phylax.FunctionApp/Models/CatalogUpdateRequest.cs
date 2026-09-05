namespace Phylax.FunctionApp.Models;

public class CatalogUpdateRequest
{
    public string MachineName { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public List<InstalledApp> InstalledApplications { get; set; } = [];

    /// <summary>
    /// The connector's own resolved delivery mode ("wsus" | "winget" | "inventory-only" - see
    /// Phylax.Connector's Worker.cs). Additive field: older connectors that don't send it just
    /// leave this empty, which the server treats as "unknown, not WSUS-managed" rather than
    /// guessing true or false.
    /// </summary>
    public string DeliveryMode { get; set; } = string.Empty;
}

public class InstalledApp
{
    public string DisplayName { get; set; } = string.Empty;
    public string DisplayVersion { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string? ProductCode { get; set; }
    public string? WingetId { get; set; }
    public string? InstallLocation { get; set; }
    public DateTime? InstallDate { get; set; }
}
