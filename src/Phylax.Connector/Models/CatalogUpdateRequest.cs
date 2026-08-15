namespace Phylax.Connector.Models;

/// <summary>
/// Mirrors Phylax.FunctionApp.Models.CatalogUpdateRequest. If you change one, change both.
/// </summary>
public class CatalogUpdateRequest
{
    public string MachineName { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public List<InstalledApp> InstalledApplications { get; set; } = new();
}

public class CatalogUpdateResponse
{
    public List<CatalogUpdate> Updates { get; set; } = new();
}