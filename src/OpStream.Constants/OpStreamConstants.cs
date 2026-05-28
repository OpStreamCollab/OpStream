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
    }

    public static class ClientEvents
    {
        public const string ReceiveOp = "ReceiveOp";
        public const string ReceiveAwareness = "ReceiveAwareness";
        public const string ReceiveAwarenessUpdate = "ReceiveAwarenessUpdate";
        public const string PeerDisconnected = "PeerDisconnected";
    }

    public static class BackplaneCommands
    {
        public const string JoinDocument = "JoinDocument";
        public const string ApplyOp = "ApplyOp";
        public const string UpdateAwareness = "UpdateAwareness";
    }

    public static class BackplaneMessages
    {
        public const string OpApplied = "op_applied";
        public const string ReceiveAwarenessUpdate = "awareness_update";
        public const string PeerDisconnected = "peer_disconnected";
    }
    
    public static class Engines
    {
        public const string TextOt = "TextOtEngine";
    }


   
}
