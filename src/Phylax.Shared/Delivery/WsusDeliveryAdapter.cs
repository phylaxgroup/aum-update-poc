using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.UpdateServices.Administration;
using Phylax.Shared.Models;

namespace Phylax.Shared.Delivery
{
    /// <summary>
    /// End-to-end WSUS delivery: download payload -> build SDP -> publish -> approve.
    ///
    /// This replaces the old Phylax.Connector.Services.WsusPublisher, whose Publish()
    /// method opened a server connection and then stopped - it never called
    /// IPublisher.PublishPackage(), which is why nothing appeared in the WSUS console
    /// even on runs that didn't throw. The WsusInvalidServerException you were hitting
    /// separately is a config issue: PhylaxConnector:Wsus:ServerName wasn't set, so it
    /// defaulted to "localhost" while running on a client box that isn't the WSUS server.
    /// Fix that in appsettings/env, this class assumes GetUpdateServer() will succeed.
    /// </summary>
    public class WsusDeliveryAdapter : IPatchDeliveryAdapter
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<WsusDeliveryAdapter> _logger;
        private readonly HttpClient _httpClient;

        public string Name => "wsus";

        public WsusDeliveryAdapter(IConfiguration configuration, ILogger<WsusDeliveryAdapter> logger, HttpClient httpClient)
        {
            _configuration = configuration;
            _logger = logger;
            _httpClient = httpClient;
        }

        public bool CanHandle(DeliveryTarget target) => target.WsusManaged;

        public async Task<DeliveryResult> DeliverAsync(PatchCandidate candidate, DeliveryTarget target, CancellationToken ct)
        {
            string wsusServer = _configuration["PhylaxConnector:Wsus:ServerName"] ?? "";
            if (string.IsNullOrWhiteSpace(wsusServer))
            {
                return DeliveryResult.Fail(Name,
                    "PhylaxConnector:Wsus:ServerName is not configured. Point this at the WSUS box " +
                    "(e.g. ptg-win25), not the machine the connector/orchestrator is running on.");
            }

            int wsusPort = int.Parse(_configuration["PhylaxConnector:Wsus:Port"] ?? "8530");
            bool useSsl = bool.Parse(_configuration["PhylaxConnector:Wsus:UseSsl"] ?? "false");
            string targetGroupName = _configuration["PhylaxConnector:Wsus:TargetGroupName"] ?? "All Computers";
            bool autoApprove = bool.Parse(_configuration["PhylaxConnector:Wsus:AutoApprove"] ?? "false");
            string stagingRoot = _configuration["PhylaxConnector:PayloadStagingPath"] ?? @"C:\ProgramData\Phylax\Staging";
            string sdpRoot = _configuration["PhylaxConnector:SdpOutputPath"] ?? @"C:\ProgramData\Phylax\SDP";

            string updateTitle = $"{candidate.ApplicationName} {candidate.NewVersion}";

            try
            {
                string payloadPath = await FetchPayloadAsync(candidate, stagingRoot, ct);
                string sdpPath = BuildSdp(candidate, payloadPath, sdpRoot, updateTitle);

                IUpdateServer server = AdminProxy.GetUpdateServer(wsusServer, useSsl, wsusPort);
                IPublisher publisher = server.GetPublisher(sdpPath);

                // The call the old stub was missing - uploads SDP metadata + content files
                // and registers the update on the WSUS server.
                publisher.PublishPackage(sdpPath, Path.GetDirectoryName(payloadPath));
                _logger.LogInformation("Published {Title} to WSUS on {Server}", updateTitle, wsusServer);

                if (autoApprove)
                {
                    var update = server.GetUpdates().OfType<IUpdate>().FirstOrDefault(u => u.Title == updateTitle);
                    if (update is not null)
                    {
                        var group = server.GetComputerTargetGroups().OfType<IComputerTargetGroup>()
                            .FirstOrDefault(g => g.Name == targetGroupName)
                            ?? throw new InvalidOperationException(
                                $"WSUS target group '{targetGroupName}' not found. Create it in the WSUS " +
                                "console or set PhylaxConnector:Wsus:TargetGroupName to an existing group.");

                        update.Approve(UpdateApprovalAction.Install, group);
                        _logger.LogInformation("Approved {Title} for install on group {Group}", updateTitle, targetGroupName);
                    }
                    else
                    {
                        _logger.LogWarning("Published {Title} but couldn't locate it to auto-approve - approve manually.", updateTitle);
                    }
                }

                return DeliveryResult.Ok(Name, $"Published{(autoApprove ? " and approved" : "")}: {updateTitle}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WSUS delivery failed for {Title}", updateTitle);
                return DeliveryResult.Fail(Name, ex.Message);
            }
        }

        private async Task<string> FetchPayloadAsync(PatchCandidate candidate, string stagingRoot, CancellationToken ct)
        {
            string targetFolder = Path.Combine(stagingRoot, candidate.VendorName, candidate.ApplicationName, candidate.NewVersion);
            Directory.CreateDirectory(targetFolder);

            string fileName = Path.GetFileName(new Uri(candidate.InstallerUrl).LocalPath);
            string fullPath = Path.Combine(targetFolder, fileName);

            if (File.Exists(fullPath))
            {
                return fullPath;
            }

            using var response = await _httpClient.GetAsync(candidate.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            using var responseStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
            await responseStream.CopyToAsync(fileStream, 81920);

            return fullPath;
        }

        private static string BuildSdp(PatchCandidate candidate, string payloadFilePath, string sdpOutputDir, string updateTitle)
        {
            Directory.CreateDirectory(sdpOutputDir);
            string sdpFileName = $"{candidate.ApplicationName.Replace(" ", "_")}-{candidate.NewVersion}.sdp";
            string sdpFullOutputPath = Path.Combine(sdpOutputDir, sdpFileName);

            var sdp = new SoftwareDistributionPackage
            {
                Title = updateTitle,
                Description = $"Third-party update for {candidate.ApplicationName} deployed via Phylax Vanguard" +
                               (candidate.Trigger == PatchTrigger.VulnerabilityTriggered
                                   ? $" (Defender-triggered - {string.Join(", ", candidate.RelatedCveIds)})"
                                   : "") + ".",
                VendorName = candidate.VendorName
            };
            sdp.ProductNames.Add(candidate.ApplicationName);

            if (string.Equals(candidate.InstallerType, "msi", StringComparison.OrdinalIgnoreCase))
            {
                sdp.PopulatePackageFromWindowsInstaller(payloadFilePath);
            }
            else
            {
                // The original PackageBuilder.cs referenced sdp.InstallableItems[0] on a brand-new
                // SDP with no items yet, which threw. My first attempt at a fix (CreateInstallableItem(),
                // FileHash, FileDigestAlgorithm) doesn't exist on this version of the WSUS Administration
                // API either - confirmed by your build output, not guessed this time. Rather than
                // fabricate a third guess, this throws clearly so it fails loud in your logs instead of
                // silently no-opping. Real options once you're ready to build this out:
                //   1. Wrap the .exe in an MSI (msiwrapper/Advanced Installer) before publishing - the
                //      PopulatePackageFromWindowsInstaller path above is the well-trodden one.
                //   2. Find a working non-MSI SDP sample against your installed WSUS Administration
                //      API version specifically (WSUS Package Publisher's source, if you can find a
                //      version matching your API) and port the exact InstallableItem construction from it.
                // Until one of those is done, route .exe-type updates (Notepad++, Chrome's .exe builds)
                // through WingetLocalDeliveryAdapter instead of WSUS - MSI-type updates (7-Zip) can use
                // this path today.
                throw new NotSupportedException(
                    $"WsusDeliveryAdapter does not yet support non-MSI installers ({candidate.ApplicationName}, " +
                    $"type={candidate.InstallerType}). Use WingetLocalDeliveryAdapter for this app, or supply " +
                    "an MSI-wrapped installer.");
            }

            sdp.Save(sdpFullOutputPath);
            return sdpFullOutputPath;
        }
    }
}