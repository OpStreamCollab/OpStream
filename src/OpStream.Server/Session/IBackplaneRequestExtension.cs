using OpStream.Shared.Abstractions;

namespace OpStream.Server.Session;

/// <summary>
/// Internal hook that lets additional command families (e.g. management) plug into the
/// single backplane request handler owned by <see cref="DocumentRouter"/> without
/// requiring the backplane to support multiple registrations.
/// </summary>
public interface IBackplaneRequestExtension
{
    /// <summary>
    /// Returns <c>true</c> when this extension recognizes the supplied
    /// <see cref="BackplaneRequest.Type"/>.
    /// </summary>
    bool CanHandle(string type);

    /// <summary>
    /// Processes the request. Only called when <see cref="CanHandle"/> returned <c>true</c>.
    /// </summary>
    Task<BackplaneResponse> HandleAsync(BackplaneRequest request, CancellationToken ct = default);
}
