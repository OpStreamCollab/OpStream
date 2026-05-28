using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpStream.Server.Session.Snapshots
{
    public class OpCountSnapshotPolicy : ISnapshotPolicy
    {
        private readonly int _ops;

        public OpCountSnapshotPolicy(int ops = 10)
        {
            _ops = ops;
        }

        public bool ShouldTakeSnapshot(SnapshotContext context)
        {
            return context.OpsSinceLastSnapshot >= _ops;                  
        }

    }
}
