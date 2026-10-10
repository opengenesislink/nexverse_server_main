// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Generic;
using System.Threading;

namespace NexVerse.Core.Experiences
{
    /// <summary>
    /// A capability URL is a bearer secret tied to a particular region,
    /// resident AND issuance. Checking only the resident UUID lets an old
    /// login URL regain authority after a logout and subsequent return to
    /// that same region. The lease invalidates the old URL independently
    /// of the viewer-facing HTTP handler's eventual removal by OpenSim.
    /// </summary>
    public sealed class NexExperienceCapLeaseRegistry
    {
        public sealed class Lease
        {
            private int m_Revoked;
            internal Lease(Guid region, Guid resident)
            {
                Region = region;
                Resident = resident;
            }

            public Guid Region { get; }
            public Guid Resident { get; }
            public bool IsRevoked => Volatile.Read(ref m_Revoked) != 0;
            internal void Revoke() => Interlocked.Exchange(ref m_Revoked, 1);
        }

        private readonly object m_Sync = new();
        private readonly Dictionary<(Guid Region, Guid Resident), Lease> m_Active = new();

        /// <summary>Reissuing even in the same region revokes prior CAPS.</summary>
        public Lease Issue(Guid region, Guid resident)
        {
            if (region == Guid.Empty || resident == Guid.Empty)
                throw new ArgumentException("A region and resident are required.");
            lock (m_Sync)
            {
                var key = (region, resident);
                if (m_Active.TryGetValue(key, out Lease old))
                    old.Revoke();
                Lease current = new(region, resident);
                m_Active[key] = current;
                return current;
            }
        }

        public bool IsCurrent(Lease lease)
        {
            if (lease == null || lease.IsRevoked)
                return false;
            lock (m_Sync)
                return !lease.IsRevoked &&
                    m_Active.TryGetValue((lease.Region, lease.Resident), out Lease current) &&
                    ReferenceEquals(current, lease);
        }

        public void InvalidateResident(Guid region, Guid resident)
        {
            if (region == Guid.Empty || resident == Guid.Empty)
                return;
            lock (m_Sync)
            {
                if (m_Active.Remove((region, resident), out Lease current))
                    current.Revoke();
            }
        }

        public void InvalidateRegion(Guid region)
        {
            if (region == Guid.Empty) return;
            lock (m_Sync)
            {
                List<(Guid Region, Guid Resident)> dead = new();
                foreach (var pair in m_Active)
                {
                    if (pair.Key.Region == region)
                    {
                        pair.Value.Revoke();
                        dead.Add(pair.Key);
                    }
                }
                foreach (var key in dead)
                    m_Active.Remove(key);
            }
        }

        public void InvalidateAll()
        {
            lock (m_Sync)
            {
                foreach (Lease lease in m_Active.Values)
                    lease.Revoke();
                m_Active.Clear();
            }
        }

        public int Count
        {
            get { lock (m_Sync) return m_Active.Count; }
        }
    }
}
