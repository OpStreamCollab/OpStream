using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpStream.Server.Session.Snapshots
{
    /// <summary>
    /// Implements a snapshot policy that triggers based on either a maximum number of operations or a maximum elapsed time.
    /// </summary>
    public class HybridSnapshotPolicy : ISnapshotPolicy
    {
        private readonly int _maxOps;
        private readonly TimeSpan _maxTime;

        /// <summary>
        /// Initializes a new instance of the <see cref="HybridSnapshotPolicy"/> class.
        /// </summary>
        /// <param name="maxOps">The maximum number of operations allowed before a snapshot is taken.</param>
        /// <param name="maxTime">The maximum time allowed before a snapshot is taken.</param>
        public HybridSnapshotPolicy(int maxOps, TimeSpan maxTime)
        {
            _maxOps = maxOps;
            _maxTime = maxTime;
        }

        /// <summary>
        /// Determines if a snapshot should be taken based on the current context.
        /// </summary>
        public bool ShouldTakeSnapshot(SnapshotContext context)
        {
            if (context.OpsSinceLastSnapshot == 0) return false;

            return context.OpsSinceLastSnapshot >= _maxOps ||
                   context.TimeSinceLastSnapshot >= _maxTime;
        }
    }
}
