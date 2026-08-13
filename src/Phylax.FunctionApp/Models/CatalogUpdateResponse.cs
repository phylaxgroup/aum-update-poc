namespace Phylax.FunctionApp.Models;

public class CatalogUpdateResponse
{
    public List<CatalogUpdate> Updates { get; set; } = [];
}

public class CatalogUpdate
{
    public string ApplicationName { get; set; } = string.Empty;
    public string CurrentVersion { get; set; } = string.Empty;
    public string NewVersion { get; set; } = string.Empty;
    public string InstallerUrl { get; set; } = string.Empty;
    public string InstallerType { get; set; } = "msi";
    public string? ProductCode { get; set; }
    public string SilentInstallArgs { get; set; } = string.Empty;
    public string Sha256Hash { get; set; } = string.Empty;

    // Metadata required for local WSUS v11 XML injection
    public string SecurityBulletinId { get; set; } = "MS26-PHY11";
    public string KbArticleId { get; set; } = "5000011";
}
