namespace Phylax.FunctionApp.Models;

public class CatalogUpdateRequest
{
    public string MachineName { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public List<InstalledApp> InstalledApplications { get; set; } = [];
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
