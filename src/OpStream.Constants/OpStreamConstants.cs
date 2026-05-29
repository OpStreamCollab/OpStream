namespace OpStream.Constants;

/// <summary>
/// Centralized constants for SignalR Hub method names and client-side events.
/// This prevents "magic strings" and ensures consistency across server and client projects.
/// </summary>
public static class OpStreamConstants
{
    public static class HubMethods
    {
        public const string JoinDocument = "JoinDocument";
        public const string SendOp = "SendOp";
        public const string UpdateAwareness = "UpdateAwareness";

        // Comments
        public const string CreateComment    = "CreateComment";
        public const string EditComment      = "EditComment";
        public const string ResolveComment   = "ResolveComment";
        public const string DeleteComment    = "DeleteComment";
        public const string ListOpenComments = "ListOpenComments";
    }

    public static class ClientEvents
    {
        public const string ReceiveOp = "ReceiveOp";
        public const string ReceiveAwareness = "ReceiveAwareness";
        public const string ReceiveAwarenessUpdate = "ReceiveAwarenessUpdate";
        public const string PeerDisconnected = "PeerDisconnected";

        // Comments
        public const string ReceiveCommentCreated = "ReceiveCommentCreated";
        public const string ReceiveCommentUpdated = "ReceiveCommentUpdated";  // edits + resolves + anchor moves
        public const string ReceiveCommentDeleted = "ReceiveCommentDeleted";
    }

    public static class BackplaneCommands
    {
        public const string JoinDocument = "JoinDocument";
        public const string ApplyOp = "ApplyOp";
        public const string UpdateAwareness = "UpdateAwareness";

        // Management commands routed to the owner node so the live session can be
        // evicted before the underlying storage is mutated.
        public const string DeleteDocument = "DeleteDocument";
        public const string CompactDocument = "CompactDocument";
        public const string PurgeHistory = "PurgeHistory";

        // Comment mutations are routed to the owner node so we can anchor at CurrentRevision
        // atomically against the session lock.
        public const string CreateComment  = "CreateComment";
        public const string EditComment    = "EditComment";
        public const string ResolveComment = "ResolveComment";
        public const string DeleteComment  = "DeleteComment";
    }

    public static class BackplaneMessages
    {
        public const string OpApplied = "op_applied";
        public const string ReceiveAwarenessUpdate = "awareness_update";
        public const string PeerDisconnected = "peer_disconnected";

        // Tenant-wide fan-out: every node closes any active session whose
        // global id starts with the supplied prefix before storage is wiped.
        public const string EvictTenant = "evict_tenant";

        // Per-document fan-out: notify all nodes that a document has been deleted,
        // so any cached state on non-owner nodes is dropped.
        public const string DocumentDeleted = "document_deleted";

        // Per-document fan-out for comment mutations. Payload is the full Comment.
        public const string CommentCreated = "comment_created";
        public const string CommentUpdated = "comment_updated";
        public const string CommentDeleted = "comment_deleted";
    }

    public static class ManagementChannels
    {
        // Pseudo document id used for cluster-wide management broadcasts.
        public const string ClusterBroadcast = "__opstream:mgmt__";
    }

    public static class ManagementHubMethods
    {
        public const string ListDocuments = "MgmtListDocuments";
        public const string GetDocumentInfo = "MgmtGetDocumentInfo";
        public const string GetSnapshot = "MgmtGetSnapshot";
        public const string DeleteDocument = "MgmtDeleteDocument";
        public const string CompactDocument = "MgmtCompactDocument";
        public const string ListMilestones = "MgmtListMilestones";
        public const string PurgeHistory = "MgmtPurgeHistory";
        public const string PurgeTenant = "MgmtPurgeTenant";
    }
    
    public static class Engines
    {
        public const string TextOt = "TextOtEngine";
    }

    public static class VersioningHubMethods
    {
        // Names
        public const string RegisterName = "VRegisterName";
        public const string ListNames    = "VListNames";

        // Branches
        public const string ListBranches  = "VListBranches";
        public const string ForkBranch    = "VForkBranch";
        public const string DeleteBranch  = "VDeleteBranch";

        // Versions / tags
        public const string CreateVersion       = "VCreateVersion";
        public const string ListVersions        = "VListVersions";
        public const string ReadVersionSnapshot = "VReadVersionSnapshot";

        // Merge
        public const string MergeBranch    = "VMergeBranch";
        public const string DryRunMerge    = "VDryRunMerge";
    }
}
