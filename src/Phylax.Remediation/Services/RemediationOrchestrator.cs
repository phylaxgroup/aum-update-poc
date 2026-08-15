using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Phylax.Shared.Catalog;
using Phylax.Shared.Delivery;
using Phylax.Shared.Models;

namespace Phylax.Remediation.Services
{
    /// <summary>
    /// Trigger -> Match -> Deliver, for the Defender-driven remediation path.
    /// Vanguard's scheduled path (CatalogUpdatesFunction) is intentionally separate and doesn't
    /// go through here — both should eventually converge on IPatchDeliveryAdapter, but the
    /// discovery/trigger side stays distinct since "scheduled scan" and "CVE just got flagged"
    /// are genuinely different decision processes (one's a diff, the other's a severity/policy call).
    /// </summary>
    public class RemediationOrchestrator
    {
        private readonly DefenderVulnerabilityService _defender;
        private readonly DeliveryAdapterResolver _resolver;
        private readonly ILogger<RemediationOrchestrator> _logger;

        // Only auto-remediate at or above this severity by default; anything below gets logged
        // but not pushed. Tune via config once this leaves POC — hardcoded here to keep the sketch readable.
        private const double MinSeverityForAutoRemediate = 7.0;

        public RemediationOrchestrator(
            DefenderVulnerabilityService defender,
            DeliveryAdapterResolver resolver,
            ILogger<RemediationOrchestrator> logger)
        {
            _defender = defender;
            _resolver = resolver;
            _logger = logger;
        }

        public async Task<List<DeliveryResult>> RunAsync(CancellationToken ct)
        {
            var findings = await _defender.GetMachineVulnerabilitiesAsync(ct);
            var results = new List<DeliveryResult>();

            // Group by machine + resolved app so multiple CVEs against the same install
            // produce one remediation action, not one per CVE.
            var grouped = findings
                .Select(f => new { Finding = f, Match = CatalogMatcher.TryResolve(f) })
                .Where(x => x.Match is not null)
                .GroupBy(x => (x.Finding.MachineId, x.Match!.Value.WingetId));

            foreach (var group in grouped)
            {
                var findingsForGroup = group.Select(x => x.Finding).ToList();
                var (wingetId, appName, installerType) = group.First().Match!.Value;
                double maxSeverity = findingsForGroup.Max(f => f.CvssScore);

                if (maxSeverity < MinSeverityForAutoRemediate)
                {
                    _logger.LogInformation(
                        "{App} on {Machine} flagged ({Cves}) but below auto-remediate threshold ({Score}) — logging only",
                        appName, group.Key.MachineId, string.Join(",", findingsForGroup.Select(f => f.Id)), maxSeverity);
                    continue;
                }

                var candidate = new PatchCandidate
                {
                    ApplicationName = appName,
                    WingetId = wingetId,
                    InstallerType = installerType,
                    NewVersion = "latest", // winget resolves this itself; WSUS path needs a real version — see note below
                    Trigger = PatchTrigger.VulnerabilityTriggered,
                    RelatedCveIds = findingsForGroup.Select(f => f.Id).ToList(),
                    MaxCvssSeverity = maxSeverity
                };

                // TODO: resolve the actual DeliveryTarget (WsusManaged / HasVanguardAgent / Arc info)
                // for group.Key.MachineId — this needs a lookup against your device inventory
                // (InventoryStorageService, extended with machine metadata) or Defender's own
                // /machines/{id} response, which includes AAD device ID you can cross-reference.
                // Stubbed here so the shape of the orchestrator is clear without guessing your
                // inventory schema.
                var target = new DeliveryTarget
                {
                    MachineName = findingsForGroup.First().ComputerDnsName ?? group.Key.MachineId
                    // WsusManaged / HasVanguardAgent / IsArcEnabled / AzureResourceId: fill in from your inventory lookup
                };

                var adapter = _resolver.Resolve(target);
                if (adapter is null)
                {
                    _logger.LogWarning("No delivery adapter could handle {Machine} for {App} — skipping", target.MachineName, appName);
                    continue;
                }

                _logger.LogInformation("Remediating {App} on {Machine} via {Adapter} (CVSS {Score}, CVEs: {Cves})",
                    appName, target.MachineName, adapter.Name, maxSeverity, string.Join(",", candidate.RelatedCveIds));

                results.Add(await adapter.DeliverAsync(candidate, target, ct));
            }

            return results;
        }
    }
}
