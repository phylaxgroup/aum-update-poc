namespace Phylax.FunctionApp.Models;

public class CatalogUpdateResponse
{
    public string TenantId { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public int UpdatesAvailable { get; set; }
    public List<AvailableUpdate> Updates { get; set; } = [];
}

public class AvailableUpdate
{
    public string ApplicationName { get; set; } = string.Empty;
    public string WingetId { get; set; } = string.Empty;
    public string CurrentVersion { get; set; } = string.Empty;
    public string NewVersion { get; set; } = string.Empty;
    public string InstallerUrl { get; set; } = string.Empty;
    public string InstallerType { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public string SilentInstallArgs { get; set; } = string.Empty;
    public string Sha256Hash { get; set; } = string.Empty;
    public bool RebootRequired { get; set; }

    // --- ADDED FOR WSUS / AUM METADATA INJECTION ---
    public string KbArticleId { get; set; } = string.Empty;
    public string SecurityBulletinId { get; set; } = string.Empty;
}
