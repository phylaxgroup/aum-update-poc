using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Phylax.Connector.Services;
using Phylax.Connector.Models;

namespace Phylax.Connector;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _log;
    private readonly InventoryScanner _scanner;
    private readonly CatalogClient _catalog;
    private readonly LocalInstallerService _installer;
    private readonly IConfiguration _config;
    private readonly TimeSpan _checkInterval = TimeSpan.FromHours(6);

    public Worker(
        ILogger<Worker> log,
        InventoryScanner scanner,
        CatalogClient catalog,
        LocalInstallerService installer,
        IConfiguration config)
    {
        _log = log;
        _scanner = scanner;
        _catalog = catalog;
        _installer = installer;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // wsus            = publish SDPs to WSUS via the standalone net48 Phylax.WsusPublisher.exe;
        //                   do NOT install locally. Windows Update/AUM handles the actual install,
        //                   which is what makes updates visible in AUM compliance reporting. Run
        //                   this on ONE designated machine - publishing is a server-wide action.
        // winget          = install locally via winget. Fast, broad coverage, but invisible
        //                   to AUM update assessment.
        // inventory-only  = scan and report, take no action.
        var deliveryMode = (_config["PhylaxConnector:DeliveryMode"] ?? "winget").Trim().ToLowerInvariant();

        if (deliveryMode is not ("wsus" or "winget" or "inventory-only"))
        {
            _log.LogWarning("Unrecognized DeliveryMode '{Mode}' - falling back to 'winget'. " +
                "Valid values: wsus, winget, inventory-only.", deliveryMode);
            deliveryMode = "winget";
        }

        // Fail fast rather than discovering the publisher is missing mid-cycle.
        if (deliveryMode == "wsus")
        {
            var probePath = ResolvePublisherPath();
            if (!File.Exists(probePath))
            {
                _log.LogError(
                    "DeliveryMode is 'wsus' but Phylax.WsusPublisher.exe was not found at '{Path}'. " +
                    "Set PhylaxConnector:WsusPublisherPath, or deploy the publisher alongside the connector. " +
                    "Falling back to inventory-only.", probePath);
                deliveryMode = "inventory-only";
            }
        }

        _log.LogInformation("Phylax Connector Service started on machine: {Machine} (delivery mode: {Mode})",
            Environment.MachineName, deliveryMode);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _log.LogInformation("--- Starting Phylax Update & Scanning Cycle ---");

                // 1. Scan local server ARP table registry baselines
                List<InstalledApp> inventory = _scanner.GetInstalledApplications();

                // 2. Diff local inventory against cloud Function App catalog definitions
                List<CatalogUpdate> availableUpdates = await _catalog.GetRequiredUpdatesAsync(inventory, deliveryMode, stoppingToken);

                if (deliveryMode == "inventory-only")
                {
                    _log.LogInformation("Inventory-only mode: {Count} update(s) identified, taking no action.",
                        availableUpdates.Count);
                }
                else
                {
                    int processedCount   = 0;
                    int approvedCount    = 0;
                    int notApprovedCount = 0;

                    foreach (var update in availableUpdates)
                    {
                        _log.LogInformation("Processing required update: {App} -> v{Version} (KB: {KB})",
                            update.ApplicationName, update.NewVersion, update.KbArticleId);

                        if (deliveryMode == "wsus")
                        {
                            var outcome = await PublishToWsusAsync(update, stoppingToken);

                            if (outcome != WsusPublishOutcome.Failed) processedCount++;
                            if (outcome == WsusPublishOutcome.PublishedAndApproved) approvedCount++;
                            if (outcome == WsusPublishOutcome.PublishedNotApproved) notApprovedCount++;
                        }
                        else if (await _installer.InstallUpdateAsync(update, stoppingToken))
                        {
                            processedCount++;
                        }
                    }

                    if (deliveryMode == "wsus")
                    {
                        // Report what actually happened rather than unconditionally telling the
                        // operator to go approve things by hand. The old summary printed
                        // "Approve them in the WSUS console (or set Wsus:AutoApprove)" even
                        // immediately after auto-approve had succeeded, which is actively
                        // misleading in a log that just said APPROVED on the preceding line.
                        if (notApprovedCount > 0)
                        {
                            _log.LogWarning(
                                "Cycle complete. Published {Processed}/{Total} package(s) to WSUS; {Approved} approved, " +
                                "{NotApproved} published but NOT approved - approve those manually in the WSUS console.",
                                processedCount, availableUpdates.Count, approvedCount, notApprovedCount);
                        }
                        else if (approvedCount > 0)
                        {
                            _log.LogInformation(
                                "Cycle complete. Published and approved {Approved}/{Total} package(s) to WSUS for group '{Group}'. " +
                                "Clients will offer them on their next detection cycle.",
                                approvedCount, availableUpdates.Count, ResolveApprovalGroupName());
                        }
                        else if (processedCount > 0)
                        {
                            _log.LogInformation(
                                "Cycle complete. Published {Processed}/{Total} package(s) to WSUS. Auto-approve is off " +
                                "(PhylaxConnector:Wsus:AutoApprove) - approve them in the WSUS console for clients to receive them.",
                                processedCount, availableUpdates.Count);
                        }
                        else
                        {
                            _log.LogInformation("Cycle complete. No packages were published ({Total} attempted).",
                                availableUpdates.Count);
                        }
                    }
                    else
                    {
                        _log.LogInformation("Cycle complete. Successfully evaluated and patched {Processed}/{Total} applications on local endpoint.",
                            processedCount, availableUpdates.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "An unhandled exception occurred during the Phylax update cycle.");
            }

            // =====================================================================
            // NOTE FOR SCHEDULED TASKS / ONE-SHOT EXECUTION:
            // If running via Task Scheduler instead of a Windows Service, uncomment
            // the break statement below so the binary exits after one complete run:
            // break;
            // =====================================================================

            await Task.Delay(_checkInterval, stoppingToken);
        }
    }

    /// <summary>
    /// Resolves the path to the standalone net48 publisher. Defaults to sitting alongside
    /// the connector binary; override with PhylaxConnector:WsusPublisherPath.
    /// </summary>
    private string ResolvePublisherPath()
    {
        var configured = _config["PhylaxConnector:WsusPublisherPath"];
        return !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, "Phylax.WsusPublisher.exe");
    }

    /// <summary>
    /// Invokes Phylax.WsusPublisher.exe as a child process.
    ///
    /// This runs out-of-process (rather than referencing the WSUS API directly) because
    /// Microsoft.UpdateServices.Administration is a .NET Framework assembly that cannot be
    /// loaded from modern .NET - verified 2026-08-16: identical code connects fine on net48 and
    /// throws FileNotFoundException on net8.0-windows. (2026-08-22: connector moved to
    /// net10.0-windows; this is a .NET Framework-vs-modern-.NET assembly loading issue, not
    /// specific to net8 - the incompatibility is presumed to still apply on net10.0-windows,
    /// not independently re-verified there.) The publisher is a minimal net48 binary that
    /// exists solely to cross that boundary. Do not "simplify" this back into the connector.
    /// </summary>
    private async Task<WsusPublishOutcome> PublishToWsusAsync(CatalogUpdate update, CancellationToken ct)
    {
        var publisherPath = ResolvePublisherPath();
        bool autoApproveRequested = IsAutoApproveEnabled();

        var args = new List<string>
        {
            "--app",      Quote(update.ApplicationName),
            "--version",  Quote(update.NewVersion),
            "--url",      Quote(update.InstallerUrl),
            "--type",     Quote(update.InstallerType),
            "--kb",       Quote(update.KbArticleId),
            "--bulletin", Quote(update.SecurityBulletinId),
            "--vendor",   Quote(_config["PhylaxConnector:VendorName"]  ?? "Phylax"),
            "--product",  Quote(_config["PhylaxConnector:ProductName"] ?? "Phylax Third-Party Updates"),
            "--staging",  Quote(_config["PhylaxConnector:PayloadStagingPath"] ?? @"C:\ProgramData\Phylax\Staging")
        };

        if (!string.IsNullOrWhiteSpace(update.SilentInstallArgs))
        {
            args.Add("--args"); args.Add(Quote(update.SilentInstallArgs));
        }

        if (!string.IsNullOrWhiteSpace(update.Sha256Hash))
        {
            args.Add("--sha256"); args.Add(Quote(update.Sha256Hash));
        }

        if (!string.IsNullOrWhiteSpace(update.WingetId))
        {
            args.Add("--wingetid"); args.Add(Quote(update.WingetId));
        }

        var wsusServer = _config["PhylaxConnector:Wsus:ServerName"];
        if (!string.IsNullOrWhiteSpace(wsusServer))
        {
            args.Add("--wsusserver"); args.Add(Quote(wsusServer));
            args.Add("--wsusport");   args.Add(_config["PhylaxConnector:Wsus:Port"] ?? "8530");
            args.Add("--wsusssl");    args.Add(_config["PhylaxConnector:Wsus:UseSsl"] ?? "false");
        }

        if (autoApproveRequested)
        {
            args.Add("--approve");
            args.Add(Quote(ResolveApprovalGroupName()));
        }

        var psi = new ProcessStartInfo
        {
            FileName               = publisherPath,
            Arguments              = string.Join(" ", args),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        _log.LogDebug("Invoking publisher: {Exe} {Args}", publisherPath, psi.Arguments);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);

        // Exit codes are defined in Phylax.WsusPublisher/Program.cs:
        //   0=ok 1=bad args 2=download failed 3=sdp failed 4=publish failed 5=published-but-not-approved
        switch (process.ExitCode)
        {
            case 0:
                _log.LogInformation("Published {App} {Version} to WSUS.{Detail}",
                    update.ApplicationName, update.NewVersion, FormatDetail(stdout));

                // Exit 0 means the publish succeeded. Whether it was also approved depends on
                // whether we asked for approval at all - the publisher only runs its approval
                // step when --approve was passed.
                return autoApproveRequested
                    ? WsusPublishOutcome.PublishedAndApproved
                    : WsusPublishOutcome.Published;

            case 5:
                // The package IS in WSUS - only the approval step failed. Still counts toward the
                // published total, but it must be distinguishable in the cycle summary so the
                // operator knows something genuinely needs manual attention.
                _log.LogWarning("Published {App} {Version} but approval failed - approve manually in the WSUS console.{Detail}",
                    update.ApplicationName, update.NewVersion, FormatDetail(stderr));
                return WsusPublishOutcome.PublishedNotApproved;

            default:
                _log.LogError("Publisher failed for {App} {Version} (exit {Code}: {Meaning}).{Detail}",
                    update.ApplicationName, update.NewVersion, process.ExitCode,
                    DescribeExitCode(process.ExitCode), FormatDetail(stderr, stdout));
                return WsusPublishOutcome.Failed;
        }
    }

    /// <summary>Whether PhylaxConnector:Wsus:AutoApprove is set to true.</summary>
    private bool IsAutoApproveEnabled() =>
        bool.TryParse(_config["PhylaxConnector:Wsus:AutoApprove"], out var autoApprove) && autoApprove;

    /// <summary>
    /// The WSUS computer target group approvals are made against. Single source of truth for both
    /// the --approve argument and the cycle summary, so the log can never name a different group
    /// than the one actually used.
    /// </summary>
    private string ResolveApprovalGroupName() =>
        _config["PhylaxConnector:Wsus:TargetGroupName"] ?? "All Computers";

    /// <summary>
    /// Result of one publish attempt, mapped from Phylax.WsusPublisher.exe's exit code. Exists so
    /// the cycle summary can report what actually happened instead of assuming - publish success
    /// and approval success are separate outcomes, and conflating them is what produced a summary
    /// telling operators to approve packages by hand immediately after auto-approve succeeded.
    /// </summary>
    private enum WsusPublishOutcome
    {
        /// <summary>Publisher returned a failure exit code; nothing is in WSUS.</summary>
        Failed,

        /// <summary>Published successfully; approval was not requested (AutoApprove off).</summary>
        Published,

        /// <summary>Published and approved for the configured target group.</summary>
        PublishedAndApproved,

        /// <summary>Published, but the approval step failed (exit 5) - needs manual approval.</summary>
        PublishedNotApproved
    }

    private static string DescribeExitCode(int code) => code switch
    {
        1 => "bad or missing arguments",
        2 => "download or hash verification failed",
        3 => "SDP build failed",
        4 => "WSUS publish failed",
        _ => "unknown error"
    };

    private static string FormatDetail(params StringBuilder[] buffers)
    {
        var text = string.Join(Environment.NewLine,
            buffers.Select(b => b.ToString().Trim()).Where(s => s.Length > 0));

        return string.IsNullOrEmpty(text) ? string.Empty : Environment.NewLine + text;
    }

    private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "") + "\"";
}