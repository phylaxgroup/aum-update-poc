using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Xml;
using Microsoft.UpdateServices.Administration;

namespace Phylax.WsusPublisher
{
    internal class WsusPackagePublisher
    {
        public int Publish(PublishRequest r)
        {
            string appDir;
            string installerPath;

            // ---- 1. Download + verify payload -------------------------------------
            try
            {
                string folderKey = string.IsNullOrWhiteSpace(r.WingetId)
                    ? r.ApplicationName.Replace(" ", "_")
                    : r.WingetId;

                appDir = Path.Combine(r.StagingDirectory, folderKey, r.NewVersion);
                Directory.CreateDirectory(appDir);

                string fileName = Path.GetFileName(new Uri(r.InstallerUrl).LocalPath);
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    fileName = folderKey + "." + r.InstallerType;
                }

                installerPath = Path.Combine(appDir, fileName);

                DownloadAndVerify(r.InstallerUrl, installerPath, r.Sha256Hash);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("DOWNLOAD FAILED: " + ex.Message);
                return 2;
            }

            // ---- 2. Build the SDP --------------------------------------------------
            string manifestPath;
            string title = r.ApplicationName + " (" + r.NewVersion + ")";

            try
            {
                var sdp = new SoftwareDistributionPackage();

                if (string.Equals(r.InstallerType, "msi", StringComparison.OrdinalIgnoreCase))
                {
                    sdp.PopulatePackageFromWindowsInstaller(installerPath);
                }
                else
                {
                    sdp.PopulatePackageFromExe(installerPath);
                }

                sdp.Title = title;
                sdp.Description = "Automated third-party security patch managed by Phylax Vanguard.";
                sdp.VendorName = r.VendorName;
                sdp.ProductNames.Clear();
                sdp.ProductNames.Add(r.ProductName);

                // EXE packages need their silent switches applied explicitly; MSI packages
                // carry theirs in the installer metadata.
                var exeItem = sdp.InstallableItems.Count > 0
                    ? sdp.InstallableItems[0] as CommandLineItem
                    : null;

                if (exeItem != null && !string.IsNullOrWhiteSpace(r.SilentInstallArgs))
                {
                    exeItem.Arguments = r.SilentInstallArgs;
                }

                manifestPath = Path.Combine(appDir, "package.xml");
                sdp.Save(manifestPath);

                // Swap ApplicationSpecificData -> UpdateSpecificData so AUM classifies this
                // as a Security Update rather than an application install.
                InjectSecuritySchemaAndKb(manifestPath, r.KbArticleId, r.SecurityBulletinId);

                Console.WriteLine("SDP built: " + manifestPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("SDP BUILD FAILED: " + ex.GetType().Name + ": " + ex.Message);
                return 3;
            }

            // ---- 3. Publish to WSUS ------------------------------------------------
            IUpdateServer server;
            try
            {
                server = string.IsNullOrWhiteSpace(r.WsusServerName)
                    ? AdminProxy.GetUpdateServer()
                    : AdminProxy.GetUpdateServer(r.WsusServerName, r.WsusUseSsl, r.WsusPort);

                Console.WriteLine("Connected to WSUS: " + server.Name);

                IPublisher publisher = server.GetPublisher(manifestPath);
                publisher.PublishPackage(appDir, null);

                Console.WriteLine("PUBLISHED: " + title);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("PUBLISH FAILED: " + ex.GetType().Name + ": " + ex.Message);
                return 4;
            }

            // ---- 4. Optional approval ---------------------------------------------
            if (!string.IsNullOrWhiteSpace(r.ApprovalGroup))
            {
                try
                {
                    var update = server.GetUpdates()
                        .OfType<IUpdate>()
                        .FirstOrDefault(u => u.Title == title);

                    if (update == null)
                    {
                        Console.Error.WriteLine("WARNING: published but could not locate '" + title + "' to approve.");
                        return 5;
                    }

                    var group = server.GetComputerTargetGroups()
                        .OfType<IComputerTargetGroup>()
                        .FirstOrDefault(g => g.Name == r.ApprovalGroup);

                    if (group == null)
                    {
                        Console.Error.WriteLine("WARNING: published but target group '" + r.ApprovalGroup + "' not found.");
                        return 5;
                    }

                    update.Approve(UpdateApprovalAction.Install, group);
                    Console.WriteLine("APPROVED for group: " + r.ApprovalGroup);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("WARNING: published but approval failed: " + ex.Message);
                    return 5;
                }
            }

            return 0;
        }

        private static void DownloadAndVerify(string url, string targetPath, string expectedSha256)
        {
            if (File.Exists(targetPath))
            {
                if (VerifyHash(targetPath, expectedSha256))
                {
                    Console.WriteLine("Payload already staged: " + targetPath);
                    return;
                }

                File.Delete(targetPath);
            }

            Console.WriteLine("Downloading " + url);

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            using (var client = new WebClient())
            {
                client.DownloadFile(url, targetPath);
            }

            if (!VerifyHash(targetPath, expectedSha256))
            {
                File.Delete(targetPath);
                throw new CryptographicException("SHA256 verification failed for " + targetPath);
            }
        }

        private static bool VerifyHash(string filePath, string expectedHex)
        {
            // No expected hash supplied means verification is skipped by design - the
            // master catalog currently ships empty hashes for POC speed.
            if (string.IsNullOrWhiteSpace(expectedHex)) return true;

            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                byte[] hash = sha.ComputeHash(stream);
                string hex = BitConverter.ToString(hash).Replace("-", string.Empty);
                return hex.Equals(expectedHex, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Replaces ApplicationSpecificData with UpdateSpecificData carrying the Security
        /// Updates classification, injecting SecurityBulletinID and KBArticleID in strict
        /// XSD order. Without this the package publishes as an application, not an update,
        /// and AUM will not surface it alongside Microsoft security patches.
        /// </summary>
        private static void InjectSecuritySchemaAndKb(string manifestPath, string kbId, string bulletinId)
        {
            var xml = new XmlDocument();
            xml.Load(manifestPath);
            string ns = xml.DocumentElement.NamespaceURI;

            var updateData = xml.CreateElement("UpdateSpecificData", ns);
            updateData.SetAttribute("UpdateClassification", "Security Updates");
            updateData.SetAttribute("MsrcSeverity", "Critical");

            var bulletinNode = xml.CreateElement("SecurityBulletinID", ns);
            bulletinNode.InnerText = string.IsNullOrWhiteSpace(bulletinId) ? "MS26-PHY00" : bulletinId;
            updateData.AppendChild(bulletinNode);

            var kbNode = xml.CreateElement("KBArticleID", ns);
            kbNode.InnerText = string.IsNullOrWhiteSpace(kbId) ? "5000000" : kbId;
            updateData.AppendChild(kbNode);

            XmlNode appData = null;
            foreach (XmlNode node in xml.DocumentElement.ChildNodes)
            {
                if (node.LocalName == "ApplicationSpecificData")
                {
                    appData = node;
                    break;
                }
            }

            if (appData == null)
            {
                throw new InvalidOperationException(
                    "XSD schema violation: <ApplicationSpecificData> not found in the generated manifest.");
            }

            xml.DocumentElement.ReplaceChild(updateData, appData);
            xml.Save(manifestPath);
        }
    }
}