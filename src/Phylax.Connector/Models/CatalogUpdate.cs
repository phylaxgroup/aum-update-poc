namespace Phylax.Connector.Models;

public class CatalogUpdate
{
    public string ApplicationName { get; set; } = string.Empty;
    public string NewVersion { get; set; } = string.Empty;
    public string InstallerUrl { get; set; } = string.Empty;
    public string InstallerType { get; set; } = "msi";
    public string? ProductCode { get; set; }
    public string WingetId { get; set; } = string.Empty;
    public string SilentInstallArgs { get; set; } = string.Empty;
    public string Sha256Hash { get; set; } = string.Empty;
    public string SecurityBulletinId { get; set; } = "MS26-PHY11";
    public string KbArticleId { get; set; } = "5000011";
}