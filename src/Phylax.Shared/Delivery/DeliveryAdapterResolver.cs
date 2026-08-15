using System;
using System.Collections.Generic;
using System.Linq;
using Phylax.Shared.Models;

namespace Phylax.Shared.Delivery
{
    /// <summary>
    /// Picks one adapter for a target from whatever's registered in DI. Order of
    /// registration matters as a tiebreaker — register WSUS first so WSUS-managed
    /// boxes keep using WSUS even if they'd also technically qualify for Arc push.
    /// </summary>
    public class DeliveryAdapterResolver
    {
        private readonly IReadOnlyList<IPatchDeliveryAdapter> _adapters;

        public DeliveryAdapterResolver(IEnumerable<IPatchDeliveryAdapter> adapters)
        {
            _adapters = adapters.ToList();
        }

        public IPatchDeliveryAdapter? Resolve(DeliveryTarget target) =>
            _adapters.FirstOrDefault(a => a.CanHandle(target));

        public IReadOnlyList<IPatchDeliveryAdapter> All => _adapters;
    }
}
