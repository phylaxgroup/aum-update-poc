using System.Threading;
using System.Threading.Tasks;
using Phylax.Shared.Models;

namespace Phylax.Shared.Delivery
{
    /// <summary>
    /// One delivery mechanism for getting a PatchCandidate onto a DeliveryTarget.
    /// Vanguard's scheduled path and the Defender-triggered remediation path both
    /// resolve to one of these — neither needs to know how the other decided to patch something.
    /// </summary>
    public interface IPatchDeliveryAdapter
    {
        /// <summary>Short identifier used in logs/telemetry, e.g. "wsus", "winget-local", "winget-arc".</summary>
        string Name { get; }

        /// <summary>Whether this adapter is capable of servicing the given target at all.</summary>
        bool CanHandle(DeliveryTarget target);

        Task<DeliveryResult> DeliverAsync(PatchCandidate candidate, DeliveryTarget target, CancellationToken ct);
    }
}
