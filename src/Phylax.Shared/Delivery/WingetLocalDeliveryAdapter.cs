using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Phylax.Shared.Models;

namespace Phylax.Shared.Delivery
{
    /// <summary>
    /// Runs "winget upgrade" directly on the box this adapter is executing on.
    /// This is meant to run INSIDE the Phylax.Connector Worker loop, as the delivery
    /// path for machines that have the Vanguard agent but aren't WSUS-managed
    /// (or where you'd rather bypass WSUS for speed on a specific app).
    ///
    /// This is the "tap into the existing vanguard winget install" option from your notes -
    /// no separate push mechanism needed since the agent is already local.
    ///
    /// Requires: winget available in PATH for the account running the service (App Installer
    /// package). If the connector runs as LocalSystem, winget may not be present/callable -
    /// worth testing on ptg-win25-client specifically since that's what bit you with WSUS.
    /// </summary>
    public class WingetLocalDeliveryAdapter : IPatchDeliveryAdapter
    {
        private readonly ILogger<WingetLocalDeliveryAdapter> _logger;

        public string Name => "winget-local";

        public WingetLocalDeliveryAdapter(ILogger<WingetLocalDeliveryAdapter> logger)
        {
            _logger = logger;
        }

        public bool CanHandle(DeliveryTarget target) => target.HasVanguardAgent && !string.IsNullOrEmpty(target.MachineName);

        public async Task<DeliveryResult> DeliverAsync(PatchCandidate candidate, DeliveryTarget target, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(candidate.WingetId))
            {
                return DeliveryResult.Fail(Name, $"{candidate.ApplicationName} has no WingetId - cannot resolve via winget.");
            }

            var psi = new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = $"upgrade --id \"{candidate.WingetId}\" --silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _logger.LogInformation("Running: winget {Args}", psi.Arguments);

            using var process = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await Task.Run(() => process.WaitForExit(), ct);

            // winget exit code 0 = success; -1978335189 (0x8A15002B) = no applicable update
            // (already current) - treat that as success too, not a failure.
            bool success = process.ExitCode == 0 || process.ExitCode == unchecked((int)0x8A15002B);

            _logger.LogInformation("winget exited {Code} for {App}", process.ExitCode, candidate.ApplicationName);

            return success
                ? DeliveryResult.Ok(Name, $"winget upgrade completed for {candidate.WingetId} (exit {process.ExitCode})")
                : DeliveryResult.Fail(Name, $"winget exit {process.ExitCode}: {stderr}");
        }
    }
}