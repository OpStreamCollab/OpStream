using OpStream.Constants;
using OpStream.Shared.Abstractions;
using System.Text.Json;

namespace OpStream.Server.Engine.Ephemeral;

/// <summary>
/// <see cref="IEphemeralChannel{TState}"/> implemented over <see cref="IBackplane"/>.
/// Serializes <typeparamref name="TState"/> with <see cref="OpStreamJsonOptions.Default"/>
/// and tags each <see cref="BackplaneMessage"/> with a configured message type so
/// downstream relays can route it.
/// </summary>
public sealed class BackplaneEphemeralChannel<TState> : IEphemeralChannel<TState>
{
    private readonly IBackplane _backplane;
    private readonly string _messageType;

    public BackplaneEphemeralChannel(IBackplane backplane, string messageType)
    {
        _backplane = backplane;
        _messageType = messageType;
    }

    public Task PublishAsync(string topic, string peerId, TState state, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(state, OpStreamJsonOptions.Default);
        var message = new BackplaneMessage(_backplane.NodeId, _messageType, payload, peerId);
        return _backplane.PublishAsync(topic, message, ct);
    }
}
