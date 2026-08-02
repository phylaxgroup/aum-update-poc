namespace Phylax.FunctionApp.Models;

public class CatalogUpdateRequest
{
    public string TenantId { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public List<AppInventoryItem> Apps { get; set; } = [];
}

public class AppInventoryItem
{
    public string DisplayName { get; set; } = string.Empty;
    public string DisplayVersion { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string? ProductCode { get; set; }
    public string? WingetId { get; set; }
}