namespace Phylax.FunctionApp.Models;

public class AppUpdateItem
{
    public string DisplayName { get; set; } = string.Empty;
    public string InstalledVersion { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;
    public string WingetId { get; set; } = string.Empty;
    public string KbArticleId { get; set; } = string.Empty;
    public string SecurityBulletinId { get; set; } = string.Empty;
}
