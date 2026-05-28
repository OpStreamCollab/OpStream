namespace OpStream.Client.Transports.gRPC;

/// <summary>
/// Configuration options for the gRPC transport.
/// </summary>
public class OpStreamgRPCOptions
{
    /// <summary>
    /// Gets or sets the server address.
    /// </summary>
    public string ServerAddress { get; set; } = "https://localhost:5001";
}
