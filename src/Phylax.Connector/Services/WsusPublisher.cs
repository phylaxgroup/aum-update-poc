using System.Security.Cryptography;
using System.Xml;
using Microsoft.Extensions.Logging;
using Microsoft.UpdateServices.Administration;
using Phylax.Connector.Models;

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

            // 1. Prepare application staging directory
            var appDir = Path.Combine(_stagingDir, update.WingetId, update.NewVersion);
            Directory.CreateDirectory(appDir);

            var fileName = $"{update.WingetId}.{update.InstallerType}";
            var installerPath = Path.Combine(appDir, fileName);

            // 2. Download installer binary
            await DownloadAndVerifyInstallerAsync(update.InstallerUrl, installerPath, update.Sha256Hash, ct);

            // 3. Initialize Software Distribution Package (SDP)
            var sdp = new SoftwareDistributionPackage();
            
            if (update.InstallerType.Equals("msi", StringComparison.OrdinalIgnoreCase))
            {
                sdp.PopulatePackageFromWindowsInstaller(installerPath);
            }
            else
            {
                sdp.PopulatePackageFromExe(installerPath);
            }

            // 4. Set display metadata
            sdp.Title = $"{update.ApplicationName} ({update.NewVersion})";
            sdp.Description = "Automated security patch managed by Phylax.";
            sdp.VendorName = "Phylax";
            sdp.ProductNames.Clear();
            sdp.ProductNames.Add("Phylax Third-Party Updates");

            // 5. Apply command-line parameters strictly if the package uses an EXE wrapper
            if (sdp.InstallableItems.Count > 0 && sdp.InstallableItems[0] is CommandLineItem exeItem)
            {
                exeItem.Arguments = update.SilentInstallArgs;
            }

            // 6. Save base SDK package XML to disk
            var manifestPath = Path.Combine(appDir, "package.xml");
            sdp.Save(manifestPath);

            // 7. Execute v11 Security schema & KB injection
            InjectSecuritySchemaAndKb(manifestPath, update.KbArticleId, update.SecurityBulletinId);

            // 8. Publish package to the local WSUS server
            _log.LogInformation("Publishing package XML to WSUS Catalog...");
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

    /// <summary>
    /// Swaps ApplicationSpecificData for UpdateSpecificData with Security Updates classification,
    /// injecting SecurityBulletinID and KBArticleID in strict XSD order.
    /// </summary>
    private void InjectSecuritySchemaAndKb(string manifestPath, string kbId, string bulletinId)
    {
        _log.LogDebug("Injecting Security Updates classification and KB schema into {Path}", manifestPath);

        var xml = new XmlDocument();
        xml.Load(manifestPath);
        var ns = xml.DocumentElement!.NamespaceURI;

        var updateData = xml.CreateElement("UpdateSpecificData", ns);
        updateData.SetAttribute("UpdateClassification", "Security Updates");
        updateData.SetAttribute("MsrcSeverity", "Critical");

        var bulletinNode = xml.CreateElement("SecurityBulletinID", ns);
        bulletinNode.InnerText = string.IsNullOrWhiteSpace(bulletinId) ? "MS26-PHY00" : bulletinId;
        updateData.AppendChild(bulletinNode);

        var kbNode = xml.CreateElement("KBArticleID", ns);
        kbNode.InnerText = string.IsNullOrWhiteSpace(kbId) ? "5000000" : kbId;
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
        else
        {
            throw new InvalidOperationException("XSD schema violation: <ApplicationSpecificData> was not found in the manifest.");
        }

        xml.Save(manifestPath);
    }

    private async Task DownloadAndVerifyInstallerAsync(string url, string targetPath, string expectedSha256, CancellationToken ct)
    {
        if (File.Exists(targetPath))
        {
            if (VerifyHash(targetPath, expectedSha256)) return;
            File.Delete(targetPath);
        }

        _log.LogInformation("Downloading installer from {Url}...", url);
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        await using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(fs, ct);
        fs.Close();

        if (!VerifyHash(targetPath, expectedSha256))
        {
            File.Delete(targetPath);
            throw new CryptographicException($"SHA256 checksum verification failed for {targetPath}");
        }
    }

    private static bool VerifyHash(string filePath, string expectedHex)
    {
        if (string.IsNullOrWhiteSpace(expectedHex)) return true;

        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha.ComputeHash(stream);
        var hex = Convert.ToHexString(hash);

        return hex.Equals(expectedHex, StringComparison.OrdinalIgnoreCase);
    }
}
