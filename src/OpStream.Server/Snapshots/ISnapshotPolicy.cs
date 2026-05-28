using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpStream.Server.Snapshots
{
    public interface ISnapshotPolicy
    {
        bool ShouldTakeSnapshot(SnapshotContext context);
    }
}
