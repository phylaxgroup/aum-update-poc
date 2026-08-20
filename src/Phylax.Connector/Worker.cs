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
                List<CatalogUpdate> availableUpdates = await _catalog.GetRequiredUpdatesAsync(inventory, stoppingToken);

                if (deliveryMode == "inventory-only")
                {
                    _log.LogInformation("Inventory-only mode: {Count} update(s) identified, taking no action.",
                        availableUpdates.Count);
                }
                else
                {
                    int processedCount = 0;
                    foreach (var update in availableUpdates)
                    {
                        _log.LogInformation("Processing required update: {App} -> v{Version} (KB: {KB})",
                            update.ApplicationName, update.NewVersion, update.KbArticleId);

                        bool success = deliveryMode == "wsus"
                            ? await PublishToWsusAsync(update, stoppingToken)
                            : await _installer.InstallUpdateAsync(update, stoppingToken);

                        if (success)
                        {
                            processedCount++;
                        }
                    }

                    if (deliveryMode == "wsus")
                    {
                        _log.LogInformation("Cycle complete. Published {Processed}/{Total} package(s) to WSUS. " +
                            "Approve them in the WSUS console (or set Wsus:AutoApprove) for clients to receive them.",
                            processedCount, availableUpdates.Count);
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
    /// loaded from .NET 8 - verified 2026-08-16: identical code connects fine on net48 and
    /// throws FileNotFoundException on net8.0-windows. The publisher is a minimal net48
    /// binary that exists solely to cross that boundary. Do not "simplify" this back into
    /// the connector.
    /// </summary>
    private async Task<bool> PublishToWsusAsync(CatalogUpdate update, CancellationToken ct)
    {
        var publisherPath = ResolvePublisherPath();

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

        if (bool.TryParse(_config["PhylaxConnector:Wsus:AutoApprove"], out var autoApprove) && autoApprove)
        {
            args.Add("--approve");
            args.Add(Quote(_config["PhylaxConnector:Wsus:TargetGroupName"] ?? "All Computers"));
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
                return true;

            case 5:
                // The package IS in WSUS - only the approval step failed. Count it as a success
                // so the cycle summary isn't misleading, but surface the warning.
                _log.LogWarning("Published {App} {Version} but approval failed - approve manually in the WSUS console.{Detail}",
                    update.ApplicationName, update.NewVersion, FormatDetail(stderr));
                return true;

            default:
                _log.LogError("Publisher failed for {App} {Version} (exit {Code}: {Meaning}).{Detail}",
                    update.ApplicationName, update.NewVersion, process.ExitCode,
                    DescribeExitCode(process.ExitCode), FormatDetail(stderr, stdout));
                return false;
        }
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