using System;
using System.Collections.Generic;

namespace Phylax.Shared.Models
{
    /// <summary>
    /// A single "this app on this box needs to move to this version" decision,
    /// independent of how it was discovered (scheduled scan vs. Defender vuln finding)
    /// and independent of how it will be delivered (WSUS SDP vs. winget-direct).
    ///
    /// Both CatalogUpdatesFunction (Vanguard scheduled path) and
    /// VulnerabilityRemediationTimerFunction (Defender-triggered path) should
    /// converge on producing a List&lt;PatchCandidate&gt; and hand it to a delivery adapter.
    /// </summary>
    public class PatchCandidate
    {
        public string ApplicationName { get; set; } = string.Empty;
        public string VendorName { get; set; } = string.Empty;

        /// <summary>WinGet package identifier, e.g. "7zip.7zip". Required for the winget delivery paths.</summary>
        public string WingetId { get; set; } = string.Empty;

        public string CurrentVersion { get; set; } = string.Empty;
        public string NewVersion { get; set; } = string.Empty;

        /// <summary>Used only by the WSUS delivery path. Winget paths ignore this and let winget resolve the source.</summary>
        public string InstallerUrl { get; set; } = string.Empty;
        public string InstallerType { get; set; } = "msi"; // msi | exe | msix
        public string SilentInstallArgs { get; set; } = string.Empty;
        public string Sha256Hash { get; set; } = string.Empty;

        public string KbArticleId { get; set; } = string.Empty;
        public string SecurityBulletinId { get; set; } = string.Empty;

        public PatchTrigger Trigger { get; set; } = PatchTrigger.ScheduledScan;

        /// <summary>Populated only when Trigger == VulnerabilityTriggered. Empty otherwise.</summary>
        public List<string> RelatedCveIds { get; set; } = new();

        /// <summary>Highest CVSS severity among RelatedCveIds, if known. Null for scheduled-scan candidates.</summary>
        public double? MaxCvssSeverity { get; set; }

        public DateTimeOffset EvaluatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    public enum PatchTrigger
    {
        ScheduledScan,
        VulnerabilityTriggered
    }
}
