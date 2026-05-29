using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using OpStream.Constants;
using OpStream.Server.Session;
using OpStream.Shared.Abstractions;
using OpStream.Shared.Messages;
using System.Text.Json;
using Xunit;

using ServerJoinRequestData = OpStream.Server.Session.JoinRequestData;

namespace OpStream.Tests.Session;

public class DocumentBackplaneGatewayTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOpStream(); 
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task HandleIncomingRequestAsync_JoinDocument_ProxiesToRouter()
    {
        // Arrange
        var sp = BuildProvider();
        var gateway = sp.GetRequiredService<IDocumentBackplaneGateway>();
        var documentRouter = sp.GetRequiredService<DocumentRouter>();
        
        await documentRouter.InitializeAsync();
        // Since we are using LocalBackplane, StartAsync will register the handler.
        
        var joinData = new ServerJoinRequestData("doc-proxy-1", "text", "peer-1", ProtocolVersions.Current);
        var request = new BackplaneRequest(
            RequestId: "req-1",
            SenderNodeId: "node-1",
            Type: OpStreamConstants.BackplaneCommands.JoinDocument,
            Payload: JsonSerializer.SerializeToUtf8Bytes(joinData, OpStreamJsonOptions.Default));

        // We can't directly call HandleIncomingRequestAsync because it's private, 
        // but we can simulate the backplane sending a request.
        var backplane = sp.GetRequiredService<IBackplane>();
        
        // Act
        var response = await backplane.SendRequestAsync(backplane.NodeId, OpStreamConstants.BackplaneCommands.JoinDocument, request.Payload);

        // Assert
        response.Success.Should().BeTrue();
        
        // Router should now have the document active
        documentRouter.GetActiveDocumentIds().Should().Contain("doc-proxy-1");
        documentRouter.GetDocumentsId("peer-1").Should().Contain("doc-proxy-1");
    }

    [Fact]
    public async Task HandleIncomingRequestAsync_UnknownCommand_ReturnsError()
    {
        // Arrange
        var sp = BuildProvider();
        var documentRouter = sp.GetRequiredService<DocumentRouter>();
        await documentRouter.InitializeAsync();
        var backplane = sp.GetRequiredService<IBackplane>();
        
        // Act
        var response = await backplane.SendRequestAsync(backplane.NodeId, "Unknown.Command", ReadOnlyMemory<byte>.Empty);

        // Assert
        response.Success.Should().BeFalse();
        response.ErrorMessage.Should().Contain("Unknown request type");
    }
}
