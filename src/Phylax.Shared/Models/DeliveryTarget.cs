namespace Phylax.Shared.Models
{
    /// <summary>
    /// Describes the machine a PatchCandidate needs to land on, so the orchestrator
    /// can pick an adapter without hardcoding machine-type logic in the adapters themselves.
    /// </summary>
    public class DeliveryTarget
    {
        public string MachineName { get; set; } = string.Empty;

        /// <summary>True if this box reports into your WSUS server (i.e. Vanguard connector + WSUS group policy is live here).</summary>
        public bool WsusManaged { get; set; }

        /// <summary>True if the Phylax Vanguard connector service is installed and phoning home from this box.</summary>
        public bool HasVanguardAgent { get; set; }

        /// <summary>True if this is an Azure Arc-enabled server (on-prem/other-cloud box projected into Azure).</summary>
        public bool IsArcEnabled { get; set; }

        /// <summary>True if this is a native Azure IaaS VM.</summary>
        public bool IsAzureVm { get; set; }

        /// <summary>ARM resource ID — required for the Arc/Azure Run Command delivery adapter.</summary>
        public string? AzureResourceId { get; set; }

        /// <summary>Subscription ID, needed to build the Run Command management-plane URL.</summary>
        public string? SubscriptionId { get; set; }

        public string? ResourceGroup { get; set; }
    }
}
