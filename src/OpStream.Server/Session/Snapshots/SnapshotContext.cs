using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpStream.Server.Session.Snapshots
{
    /// <summary>
    /// Provides context information for determining if a snapshot should be taken.
    /// </summary>
    public record SnapshotContext(                                   
                                    int OpsSinceLastSnapshot,
                                    TimeSpan TimeSinceLastSnapshot 
                                    );
}
