using System;
using Moq;
using OpStream.Server.Multitenancy;
using OpStream.Shared.Abstractions;
using Xunit;

namespace OpStream.Tests.Multitenancy
{
    public class TenantAwareDocumentIdGlobalizerTests
    {
        private readonly Mock<ITenantProvider> _tenantProviderMock;
        private readonly TenantAwareDocumentIdGlobalizer _sut;

        public TenantAwareDocumentIdGlobalizerTests()
        {
            _tenantProviderMock = new Mock<ITenantProvider>();
            _sut = new TenantAwareDocumentIdGlobalizer(_tenantProviderMock.Object);
        }

        [Fact]
        public void ToGlobalId_WithValidTenantAndDocument_GeneratesCorrectly()
        {
            // Arrange
            _tenantProviderMock.Setup(x => x.GetCurrentTenantId()).Returns("tenant1");

            // Act
            var globalId = _sut.ToGlobalId("doc1");

            // Assert
            Assert.Equal("tenant1:#:doc1", globalId);
        }

        [Fact]
        public void ToLocalId_WithValidGlobalId_ExtractsLocalIdCorrectly()
        {
            // Arrange
            var globalId = "tenant1:#:doc1";

            // Act
            var localId = _sut.ToLocalId(globalId);

            // Assert
            Assert.Equal("doc1", localId);
        }

        [Fact]
        public void ToGlobalId_WithEmptyTenant_DoesNotPrependSeparator()
        {
            // Arrange
            _tenantProviderMock.Setup(x => x.GetCurrentTenantId()).Returns(string.Empty);

            // Act
            var globalId = _sut.ToGlobalId("doc1");

            // Assert
            Assert.Equal("doc1", globalId);
        }

        [Fact]
        public void ToLocalId_WithEmptyTenantGlobalId_ExtractsLocalIdCorrectly()
        {
            // Arrange
            var globalId = "doc1";

            // Act
            var localId = _sut.ToLocalId(globalId);

            // Assert
            Assert.Equal("doc1", localId);
        }

        [Fact]
        public void EdgeCase_ToGlobalId_WhenTenantContainsSeparator_DemonstratesExtractionFailure()
        {
            // Arrange
            _tenantProviderMock.Setup(x => x.GetCurrentTenantId()).Returns("tenant:#:1");
            var localId = "doc1";

            // Act
            var globalId = _sut.ToGlobalId(localId); // Result: "tenant:#:1:#:doc1"
            var extractedLocalId = _sut.ToLocalId(globalId);

            // Assert
            // This test demonstrates the current limitation: Split(":#:", 2) on "tenant:#:1:#:doc1" 
            // will return ["tenant", "1:#:doc1"], so it returns "1:#:doc1" which is wrong.
            Assert.NotEqual(localId, extractedLocalId);
            Assert.Equal("1:#:doc1", extractedLocalId);
        }

        [Fact]
        public void EdgeCase_ToLocalId_WhenDocumentContainsSeparator_ExtractsCorrectly()
        {
            // Arrange
            _tenantProviderMock.Setup(x => x.GetCurrentTenantId()).Returns("tenant1");
            var localId = "doc:#:1";

            // Act
            var globalId = _sut.ToGlobalId(localId); // Result: "tenant1:#:doc:#:1"
            var extractedLocalId = _sut.ToLocalId(globalId);

            // Assert
            // Split(":#:", 2) on "tenant1:#:doc:#:1" returns ["tenant1", "doc:#:1"]
            // So this actually works correctly by coincidence of the Split(..., 2) overload.
            Assert.Equal(localId, extractedLocalId);
        }

        [Fact]
        public void EdgeCase_ToLocalId_WhenNoSeparatorPresent_ReturnsOriginalString()
        {
            // Arrange
            var globalId = "doc1_no_tenant_or_separator";

            // Act
            var localId = _sut.ToLocalId(globalId);

            // Assert
            Assert.Equal("doc1_no_tenant_or_separator", localId);
        }

        [Fact]
        public void EdgeCase_ToGlobalId_WhenTenantIsNull_DoesNotPrependSeparator()
        {
            // Arrange
            _tenantProviderMock.Setup(x => x.GetCurrentTenantId()).Returns((string)null!);

            // Act
            var globalId = _sut.ToGlobalId("doc1");

            // Assert
            // A null tenantId is treated as an empty string.
            Assert.Equal("doc1", globalId);
        }
    }
}
