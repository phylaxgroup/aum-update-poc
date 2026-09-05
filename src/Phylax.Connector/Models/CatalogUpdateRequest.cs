namespace Phylax.Connector.Models;

/// <summary>
/// Mirrors Phylax.FunctionApp.Models.CatalogUpdateRequest. If you change one, change both.
/// </summary>
public class CatalogUpdateRequest
{
    public string MachineName { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public List<InstalledApp> InstalledApplications { get; set; } = new();

    /// <summary>The resolved DeliveryMode from Worker.cs ("wsus" | "winget" | "inventory-only").</summary>
    public string DeliveryMode { get; set; } = string.Empty;
}

public class CatalogUpdateResponse
{
    public List<CatalogUpdate> Updates { get; set; } = new();
}