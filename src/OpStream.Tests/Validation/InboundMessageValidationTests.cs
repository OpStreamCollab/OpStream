using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpStream.Constants;
using OpStream.Server.Session;
using OpStream.Server.Validation;
using OpStream.Shared.Abstractions;
using System.Text.Json;
using Xunit;

namespace OpStream.Tests.Validation;

/// <summary>
/// Covers the inbound-message validation hook: the single library-level choke point every
/// transport message passes through before the server acts on it.
/// </summary>
public class InboundMessageValidationTests
{
    private static ServiceProvider BuildProvider(Action<OpStream.Server.Models.OpStreamOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOpStream(configure);
        return services.BuildServiceProvider();
    }

    private static async Task<DocumentRouter> JoinedRouterAsync(ServiceProvider sp, string docId)
    {
        var router = sp.GetRequiredService<DocumentRouter>();
        await router.InitializeAsync();
        await router.JoinDocumentAsync(docId, "text", "peer-1", ProtocolVersions.Current);
        return router;
    }

    [Fact]
    public async Task ApplyOpAsync_EmptyPayload_IsRejected()
    {
        var sp = BuildProvider();
        var router = await JoinedRouterAsync(sp, "doc-empty");

        var result = await router.ApplyOpAsync("peer-1", "doc-empty", ReadOnlyMemory<byte>.Empty, 0);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("InvalidMessage");
    }

    [Fact]
    public async Task ApplyOpAsync_OversizedPayload_IsRejected()
    {
        var sp = BuildProvider(o => o.Validation.MaxOpPayloadBytes = 8);
        var router = await JoinedRouterAsync(sp, "doc-big");

        var payload = new byte[9];

        var result = await router.ApplyOpAsync("peer-1", "doc-big", payload, 0);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exceeds");
    }

    [Fact]
    public async Task JoinDocumentAsync_BlankDocumentId_IsRejected()
    {
        var sp = BuildProvider();
        var router = sp.GetRequiredService<DocumentRouter>();
        await router.InitializeAsync();

        var result = await router.JoinDocumentAsync("   ", "text", "peer-1", ProtocolVersions.Current);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("InvalidMessage");
    }

    [Fact]
    public async Task UpdateAwarenessAsync_OversizedData_IsRejected()
    {
        var sp = BuildProvider(o => o.Validation.MaxAwarenessBytes = 16);
        var router = await JoinedRouterAsync(sp, "doc-aware");

        var data = JsonDocument.Parse("{\"cursor\":\"" + new string('x', 64) + "\"}").RootElement;

        var result = await router.UpdateAwarenessAsync("peer-1", "doc-aware", data);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("InvalidMessage");
    }

    [Fact]
    public async Task ApplyOpAsync_ValidOp_PassesValidation()
    {
        var sp = BuildProvider();
        var router = sp.GetRequiredService<DocumentRouter>();
        await router.InitializeAsync();
        var join = await router.JoinDocumentAsync("doc-ok", "text", "peer-1", ProtocolVersions.Current);

        var op = new OpStream.Server.Engine.Text.TextOp(
            new OpStream.Server.Engine.Text.TextOpComponent[] { new OpStream.Server.Engine.Text.Insert("Hi") });
        var payload = JsonSerializer.SerializeToUtf8Bytes(op, OpStreamJsonOptions.Default);

        var result = await router.ApplyOpAsync("peer-1", "doc-ok", payload, join.Value!.Revision);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task CustomValidator_RunsAfterDefault_AndCanReject()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOpStream().AddInboundMessageValidator<RejectAllOpsValidator>();
        var sp = services.BuildServiceProvider();

        var router = await JoinedRouterAsync(sp, "doc-custom");

        var op = new OpStream.Server.Engine.Text.TextOp(
            new OpStream.Server.Engine.Text.TextOpComponent[] { new OpStream.Server.Engine.Text.Insert("Hi") });
        var payload = JsonSerializer.SerializeToUtf8Bytes(op, OpStreamJsonOptions.Default);

        var result = await router.ApplyOpAsync("peer-1", "doc-custom", payload, 0);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("blocked by policy");
    }

    private sealed class RejectAllOpsValidator : IInboundMessageValidator
    {
        public ValueTask<InboundValidationResult> ValidateAsync(InboundMessage message, CancellationToken ct = default)
            => new(message.Kind == InboundMessageKind.Op
                ? InboundValidationResult.Invalid("blocked by policy")
                : InboundValidationResult.Valid);
    }
}
