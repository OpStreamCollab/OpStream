
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using OpStream.Constants;
using OpStream.Shared.Messages;
using System.Text.Json;


namespace OpStream.Client.Transports.SignalR
{
    /// <summary>
    /// A SignalR-based implementation of the OpStream client transport.
    /// </summary>
    public class SignalROpStreamClient : IOpStreamClient
    {
        private readonly HubConnection _hubConnection;

        /// <inheritdoc/>
        public event Func<ReadOnlyMemory<byte>, long, Task>? OnReceiveOp;

        /// <inheritdoc/>
        public event Action<Exception?>? OnDisconnected;

        /// <inheritdoc/>
        public event Func<IEnumerable<AwarenessState>, Task>? OnReceiveAwareness;

        /// <inheritdoc/>
        public event Action<string>? OnPeerDisconnected;

        /// <inheritdoc/>
        public event Func<CommentDto, Task>? OnCommentCreated;

        /// <inheritdoc/>
        public event Func<CommentDto, Task>? OnCommentUpdated;

        /// <inheritdoc/>
        public event Func<CommentDeletedDto, Task>? OnCommentDeleted;

        /// <summary>
        /// Initializes a new instance of the SignalROpStreamClient.
        /// </summary>
        /// <param name="options">Configuration options containing the hub URL.</param>
        public SignalROpStreamClient(IOptions<OpStreamSignalROptions> options)
        {
            _hubConnection = new HubConnectionBuilder()
                .WithUrl(options.Value.HubUrl)
                .WithAutomaticReconnect()
                .Build();


            _hubConnection.On<byte[], long>(OpStreamConstants.ClientEvents.ReceiveOp, (payload, newRev) =>
            {
                if (OnReceiveOp != null)
                {
                    return OnReceiveOp.Invoke(payload, newRev);
                }
                return Task.CompletedTask;
            });

            _hubConnection.On<AwarenessState>(OpStreamConstants.ClientEvents.ReceiveAwarenessUpdate, (state) =>
            {
                if (OnReceiveAwareness != null)
                {
                    return OnReceiveAwareness.Invoke(new[] { state });
                }
                return Task.CompletedTask;
            });

            _hubConnection.On<string>(OpStreamConstants.ClientEvents.PeerDisconnected, (peerId) =>
            {
                OnPeerDisconnected?.Invoke(peerId);
                return Task.CompletedTask;
            });

            _hubConnection.On<CommentDto>(OpStreamConstants.ClientEvents.ReceiveCommentCreated, (comment) =>
            {
                return OnCommentCreated?.Invoke(comment) ?? Task.CompletedTask;
            });

            _hubConnection.On<CommentDto>(OpStreamConstants.ClientEvents.ReceiveCommentUpdated, (comment) =>
            {
                return OnCommentUpdated?.Invoke(comment) ?? Task.CompletedTask;
            });

            _hubConnection.On<CommentDeletedDto>(OpStreamConstants.ClientEvents.ReceiveCommentDeleted, (msg) =>
            {
                return OnCommentDeleted?.Invoke(msg) ?? Task.CompletedTask;
            });

            _hubConnection.Closed += (exception) =>
            {
                OnDisconnected?.Invoke(exception);
                return Task.CompletedTask;
            };
        }

        /// <inheritdoc/>
        public async Task<ClientJoinResult> ConnectAndJoinAsync(string documentId, string documentType, CancellationToken ct = default)
        {
            await _hubConnection.StartAsync(ct);

            // Handshake using constant
            var result = await _hubConnection.InvokeAsync<SessionJoinDto>(
                OpStreamConstants.HubMethods.JoinDocument,
                documentId, documentType, 1, cancellationToken: ct);

            return new ClientJoinResult(result.Revision, result.Snapshot, result.CurrentAwareness ?? new List<AwarenessState>());
        }

        /// <inheritdoc/>
        public async Task<ClientOpResult> SendOpAsync(string documentId, ReadOnlyMemory<byte> payload, long baseRevision, CancellationToken ct = default)
        {
            // SendOp using constant
            var result = await _hubConnection.InvokeAsync<OpApplyDto>(
                OpStreamConstants.HubMethods.SendOp,
                documentId, payload.ToArray(), baseRevision, cancellationToken: ct);
            return new ClientOpResult(result.Success, result.NewRevision, result.ErrorMessage);
        }

        /// <inheritdoc/>
        public async Task SendAwarenessAsync(string documentId, JsonElement data, CancellationToken ct = default)
        {
            await _hubConnection.InvokeAsync(
                OpStreamConstants.HubMethods.UpdateAwareness,
                documentId, data, cancellationToken: ct);
        }

        // ─── Comments ────────────────────────────────────────────────────────
        //
        // IMPORTANT: pass the CancellationToken via the `cancellationToken:`
        // named argument. If we let it bind positionally, SignalR serialises it
        // as an extra hub-method argument and the server rejects the invocation
        // with `HubException: Method does not exist`.

        /// <inheritdoc/>
        public Task<List<CommentDto>> ListOpenCommentsAsync(string documentId, CancellationToken ct = default)
            => _hubConnection.InvokeAsync<List<CommentDto>>(
                OpStreamConstants.HubMethods.ListOpenComments,
                documentId, cancellationToken: ct);

        /// <inheritdoc/>
        public Task<CommentDto> CreateCommentAsync(string documentId, NewCommentCmd cmd, CancellationToken ct = default)
            => _hubConnection.InvokeAsync<CommentDto>(
                OpStreamConstants.HubMethods.CreateComment,
                documentId, cmd, cancellationToken: ct);

        /// <inheritdoc/>
        public Task<CommentDto> EditCommentAsync(string documentId, string commentId, string newBody, CancellationToken ct = default)
            => _hubConnection.InvokeAsync<CommentDto>(
                OpStreamConstants.HubMethods.EditComment,
                documentId, commentId, newBody, cancellationToken: ct);

        /// <inheritdoc/>
        public Task<CommentDto> ResolveCommentAsync(string documentId, string commentId, CancellationToken ct = default)
            => _hubConnection.InvokeAsync<CommentDto>(
                OpStreamConstants.HubMethods.ResolveComment,
                documentId, commentId, cancellationToken: ct);

        /// <inheritdoc/>
        public Task DeleteCommentAsync(string documentId, string commentId, CancellationToken ct = default)
            => _hubConnection.InvokeAsync(
                OpStreamConstants.HubMethods.DeleteComment,
                documentId, commentId, cancellationToken: ct);

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            await _hubConnection.DisposeAsync();
        }



        // Internal DTOs to map what your current OpStreamHub returns
        private record SessionJoinDto(long Revision, ReadOnlyMemory<byte> Snapshot, List<AwarenessState>? CurrentAwareness);
        private record OpApplyDto(bool Success, long NewRevision, string? ErrorMessage);
    }
}
