using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpStream.Server.Snapshots
{
    public class TimeBasedSnapshotPolicy : ISnapshotPolicy
    {
        private readonly TimeSpan _interval;

        public TimeBasedSnapshotPolicy(TimeSpan interval)
        {
            _interval = interval;
        }

        public bool ShouldTakeSnapshot(SnapshotContext context)
        {           
            return context.OpsSinceLastSnapshot > 0 &&
                   context.TimeSinceLastSnapshot >= _interval;
        }
    }
}
