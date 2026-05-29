using Microsoft.AspNetCore.Http;
using OpStream.Constants;
using OpStream.Server.Versioning;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace OpStream.Server.Transports.WebSockets;

/// <summary>
/// WebSocket versioning endpoint. Accepts JSON-encoded <see cref="WebSocketVerRequest"/> messages
/// and responds with <see cref="WebSocketVerResponse"/> messages.
/// <para>
/// All commands are routed through <see cref="VersioningRouter"/>, which applies tenant
/// scoping and authorization — identical to the SignalR versioning hub.
/// </para>
/// </summary>
public class WebSocketVersioningTransport(VersioningRouter router)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();

        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                var json = await ReceiveTextAsync(webSocket, context.RequestAborted);
                if (json is null) break;

                var request = JsonSerializer.Deserialize<WebSocketVerRequest>(json, JsonOptions);
                if (request is not null)
                    await HandleRequestAsync(webSocket, request, context.RequestAborted);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"WebSocket versioning error: {ex.Message}");
        }
        finally
        {
            if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None); }
                catch { }
            }
        }
    }

    private async Task HandleRequestAsync(WebSocket webSocket, WebSocketVerRequest request, CancellationToken ct)
    {
        WebSocketVerResponse response;
        try
        {
            response = await DispatchAsync(request, ct);
        }
        catch (Exception ex)
        {
            response = Error(request.CorrelationId, ex.Message);
        }

        response.CorrelationId ??= request.CorrelationId;
        var json  = JsonSerializer.Serialize(response, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private async Task<WebSocketVerResponse> DispatchAsync(WebSocketVerRequest request, CancellationToken ct)
    {
        switch (request.Command)
        {
            // ── Names ─────────────────────────────────────────────────────────

            case OpStreamConstants.VersioningHubMethods.RegisterName:
            {
                Require(request.Name, nameof(request.Name), request.Command);
                Require(request.PhysicalDocumentId, nameof(request.PhysicalDocumentId), request.Command);
                Require(request.EngineType, nameof(request.EngineType), request.Command);
                var result = await router.RegisterNameAsync(
                    request.Name!, request.PhysicalDocumentId!, request.EngineType!,
                    request.RootBranchId ?? "main", ct);
                if (!result.Success) return Error(request.CorrelationId, result.ErrorMessage);
                return new WebSocketVerResponse { CorrelationId = request.CorrelationId, Success = true, NameInfo = result.Value };
            }

            case OpStreamConstants.VersioningHubMethods.ListNames:
            {
                var result = await router.ListNamesAsync(ct);
                if (!result.Success) return Error(request.CorrelationId, result.ErrorMessage);
                return new WebSocketVerResponse { CorrelationId = request.CorrelationId, Success = true, Names = result.Value };
            }

            case OpStreamConstants.VersioningHubMethods.DeleteName:
            {
                Require(request.Name, nameof(request.Name), request.Command);
                var result = await router.DeleteNameAsync(request.Name!, request.Cascade, ct);
                if (!result.Success) return Error(request.CorrelationId, result.ErrorMessage);
                return Ok(request.CorrelationId);
            }

            // ── Branches ──────────────────────────────────────────────────────

            case OpStreamConstants.VersioningHubMethods.ListBranches:
            {
                Require(request.Name, nameof(request.Name), request.Command);
                var result = await router.ListBranchesAsync(request.Name!, ct);
                if (!result.Success) return Error(request.CorrelationId, result.ErrorMessage);
                return new WebSocketVerResponse { CorrelationId = request.CorrelationId, Success = true, Branches = result.Value };
            }

            case OpStreamConstants.VersioningHubMethods.ForkBranch:
            {
                Require(request.Name,         nameof(request.Name),         request.Command);
                Require(request.FromBranchId, nameof(request.FromBranchId), request.Command);
                Require(request.NewBranchId,  nameof(request.NewBranchId),  request.Command);
                var result = await router.ForkBranchAsync(
                    request.Name!, request.FromBranchId!, request.NewBranchId!, request.AtRevision, ct);
                if (!result.Success) return Error(request.CorrelationId, result.ErrorMessage);
                return new WebSocketVerResponse { CorrelationId = request.CorrelationId, Success = true, Branch = result.Value };
            }

            case OpStreamConstants.VersioningHubMethods.DeleteBranch:
            {
                Require(request.Name,     nameof(request.Name),     request.Command);
                Require(request.BranchId, nameof(request.BranchId), request.Command);
                var result = await router.DeleteBranchAsync(request.Name!, request.BranchId!, ct);
                if (!result.Success) return Error(request.CorrelationId, result.ErrorMessage);
                return Ok(request.CorrelationId);
            }

            // ── Versions / tags ───────────────────────────────────────────────

            case OpStreamConstants.VersioningHubMethods.CreateVersion:
            {
                Require(request.Name,     nameof(request.Name),     request.Command);
                Require(request.BranchId, nameof(request.BranchId), request.Command);
                Require(request.Tag,      nameof(request.Tag),      request.Command);
                var result = await router.CreateVersionAsync(request.Name!, request.BranchId!, request.Tag!, ct);
                if (!result.Success) return Error(request.CorrelationId, result.ErrorMessage);
                return new WebSocketVerResponse { CorrelationId = request.CorrelationId, Success = true, Version = result.Value };
            }

            case OpStreamConstants.VersioningHubMethods.ListVersions:
            {
                Require(request.Name,     nameof(request.Name),     request.Command);
                Require(request.BranchId, nameof(request.BranchId), request.Command);
                var result = await router.ListVersionsAsync(request.Name!, request.BranchId!, ct);
                if (!result.Success) return Error(request.CorrelationId, result.ErrorMessage);
                return new WebSocketVerResponse { CorrelationId = request.CorrelationId, Success = true, Versions = result.Value };
            }

            case OpStreamConstants.VersioningHubMethods.ReadVersionSnapshot:
            {
                Require(request.Name,     nameof(request.Name),     request.Command);
                Require(request.BranchId, nameof(request.BranchId), request.Command);
                Require(request.Tag,      nameof(request.Tag),      request.Command);
                var result = await router.ReadVersionSnapshotAsync(request.Name!, request.BranchId!, request.Tag!, ct);
                if (!result.Success) return Error(request.CorrelationId, result.ErrorMessage);
                return new WebSocketVerResponse { CorrelationId = request.CorrelationId, Success = true, Snapshot = result.Value };
            }

            case OpStreamConstants.VersioningHubMethods.DeleteVersion:
            {
                Require(request.Name,     nameof(request.Name),     request.Command);
                Require(request.BranchId, nameof(request.BranchId), request.Command);
                Require(request.Tag,      nameof(request.Tag),      request.Command);
                var result = await router.DeleteVersionAsync(
                    request.Name!, request.BranchId!, request.Tag!, request.DropSnapshot, ct);
                if (!result.Success) return Error(request.CorrelationId, result.ErrorMessage);
                return Ok(request.CorrelationId);
            }

            // ── Merge ─────────────────────────────────────────────────────────

            case OpStreamConstants.VersioningHubMethods.MergeBranch:
            case OpStreamConstants.VersioningHubMethods.DryRunMerge:
            {
                Require(request.Name,           nameof(request.Name),           request.Command);
                Require(request.TargetBranchId, nameof(request.TargetBranchId), request.Command);
                Require(request.SourceBranchId, nameof(request.SourceBranchId), request.Command);
                bool dryRun = request.DryRun || request.Command == OpStreamConstants.VersioningHubMethods.DryRunMerge;
                var result = await router.MergeAsync(
                    request.Name!, request.TargetBranchId!, request.SourceBranchId!,
                    dryRun: dryRun, ct: ct);
                if (!result.Success) return Error(request.CorrelationId, result.ErrorMessage);
                return new WebSocketVerResponse { CorrelationId = request.CorrelationId, Success = true, MergeReport = result.Value };
            }

            default:
                return Error(request.CorrelationId, $"Unknown versioning command: '{request.Command}'.");
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static WebSocketVerResponse Ok(string? correlationId) =>
        new() { CorrelationId = correlationId, Success = true };

    private static WebSocketVerResponse Error(string? correlationId, string? message) =>
        new() { CorrelationId = correlationId, Success = false, ErrorMessage = message ?? "Unknown error" };

    private static void Require(string? value, string paramName, string command)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"'{paramName}' is required for command '{command}'.");
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket webSocket, CancellationToken ct)
    {
        var buffer = new byte[1024 * 4];
        using var ms = new MemoryStream();

        WebSocketReceiveResult result;
        do
        {
            result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        ms.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ms, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }
}
