namespace OpStream.Server.Session;

public record OpAppliedBackplanePayload(byte[] Operation, long NewRevision);
