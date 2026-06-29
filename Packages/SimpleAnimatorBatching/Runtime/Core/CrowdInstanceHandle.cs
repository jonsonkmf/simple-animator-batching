using System;

namespace SimpleAnimatorBatching
{
    /// <summary>
    /// Lightweight, copyable reference to a single pooled crowd instance.
    ///
    /// A handle is just a pool slot plus the generation that slot had when the handle was issued.
    /// The pool considers a handle valid only while the slot is still active AND its generation
    /// still matches — this guards against the classic "stale handle revived after the slot was
    /// reused by a different instance" bug. Compare against <see cref="Invalid"/> to check whether
    /// a Spawn actually produced an instance.
    /// </summary>
    public readonly struct CrowdInstanceHandle : IEquatable<CrowdInstanceHandle>
    {
        internal readonly int slot;
        internal readonly int generation;

        internal CrowdInstanceHandle(int slot, int generation)
        {
            this.slot = slot;
            this.generation = generation;
        }

        /// <summary>The handle returned when a spawn fails (e.g. the pool is full).</summary>
        public static CrowdInstanceHandle Invalid => new CrowdInstanceHandle(-1, 0);

        /// <summary>
        /// Structurally valid only — i.e. not the <see cref="Invalid"/> sentinel. This does NOT
        /// mean the instance is still alive; use the spawner's IsValid(handle) for that, since
        /// liveness also depends on the slot's current generation and active state.
        /// </summary>
        public bool IsCreated => slot >= 0;

        public bool Equals(CrowdInstanceHandle other) => slot == other.slot && generation == other.generation;
        public override bool Equals(object obj) => obj is CrowdInstanceHandle other && Equals(other);
        public override int GetHashCode() => (slot * 397) ^ generation;
        public override string ToString() => $"CrowdInstanceHandle(slot={slot}, gen={generation})";
    }
}
