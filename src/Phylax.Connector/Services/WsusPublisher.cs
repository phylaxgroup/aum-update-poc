using System.Security.Cryptography;
using System.Xml;
using Microsoft.Extensions.Logging;
using Microsoft.UpdateServices.Administration;
using Phylax.FunctionApp.Models; 

namespace Phylax.Connector.Services;

public class WsusPublisher
{
    private readonly ILogger<WsusPublisher> _log;
    private readonly HttpClient _http;
    private readonly string _stagingDir;

    public WsusPublisher(ILogger<WsusPublisher> log, IHttpClientFactory httpFactory)
    {
        _log = log;
        _http = httpFactory.CreateClient();
        _stagingDir = @"C:\Payloads\PhylaxStaging";
        Directory.CreateDirectory(_stagingDir);
    }

    public async Task<bool> PublishUpdateAsync(AvailableUpdate update, CancellationToken ct = default)
    {
        try
        {
            _log.LogInformation("Preparing to publish: {App} v{Version}", update.ApplicationName, update.NewVersion);

            var appDir = Path.Combine(_stagingDir, update.WingetId, update.NewVersion);
            Directory.CreateDirectory(appDir);

            var fileName = $"{update.WingetId}.{update.InstallerType}";
            var installerPath = Path.Combine(appDir, fileName);

            await DownloadAndVerifyInstallerAsync(update.InstallerUrl, installerPath, update.Sha256Hash, ct);

            var sdp = new SoftwareDistributionPackage();
            sdp.PopulatePackageFromWindowsInstaller(installerPath);

            sdp.Title = $"{update.ApplicationName} ({update.NewVersion})";
            sdp.Description = $"Automated security patch managed by Phylax.";
            sdp.VendorName = "Phylax";
            sdp.ProductNames.Clear();
            sdp.ProductNames.Add("Phylax Third-Party Updates");
            sdp.InstallableItems[0].InstallCommandLine = update.SilentInstallArgs;

            var manifestPath = Path.Combine(appDir, "package.xml");
            sdp.Save(manifestPath);

            InjectSecuritySchemaAndKb(manifestPath, update.KbArticleId, update.SecurityBulletinId);

            var wsus = AdminProxy.GetUpdateServer();
            var publisher = wsus.GetPublisher(manifestPath);
            publisher.PublishPackage(appDir, null);

            _log.LogInformation("Successfully published {Title} to WSUS.", sdp.Title);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to publish update for {App}", update.ApplicationName);
            return false;
        }
    }

    private void InjectSecuritySchemaAndKb(string manifestPath, string kbId, string bulletinId)
    {
        var xml = new XmlDocument();
        xml.Load(manifestPath);
        var ns = xml.DocumentElement!.NamespaceURI;

        var updateData = xml.CreateElement("UpdateSpecificData", ns);
        updateData.SetAttribute("UpdateClassification", "Security Updates");
        updateData.SetAttribute("MsrcSeverity", "Critical");

        var bulletinNode = xml.CreateElement("SecurityBulletinID", ns);
        bulletinNode.InnerText = bulletinId;
        updateData.AppendChild(bulletinNode);

        var kbNode = xml.CreateElement("KBArticleID", ns);
        kbNode.InnerText = kbId;
        updateData.AppendChild(kbNode);

        XmlNode? appData = null;
        foreach (XmlNode node in xml.DocumentElement.ChildNodes)
        {
            if (node.LocalName == "ApplicationSpecificData")
            {
                appData = node;
                break;
            }
        }

        if (appData != null)
        {
            xml.DocumentElement.ReplaceChild(updateData, appData);
        }
        xml.Save(manifestPath);
    }

    private async Task DownloadAndVerifyInstallerAsync(string url, string targetPath, string expectedSha256, CancellationToken ct)
    {
        if (File.Exists(targetPath)) return;

        _log.LogInformation("Downloading installer from {Url}...", url);
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(fs, ct);
    }
}
