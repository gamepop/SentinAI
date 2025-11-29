using Microsoft.Extensions.Logging;
using Moq;
using SentinAI.Web.Services.Rag;

namespace SentinAI.Web.Tests.Services;

public class NoopRagStoreTests
{
    private readonly Mock<ILogger<NoopRagStore>> _loggerMock;
    private readonly NoopRagStore _store;

    public NoopRagStoreTests()
    {
        _loggerMock = new Mock<ILogger<NoopRagStore>>();
        _store = new NoopRagStore(_loggerMock.Object);
    }

    [Fact]
    public void IsEnabled_ReturnsFalse()
    {
        // Assert
        Assert.False(_store.IsEnabled);
    }

    [Fact]
    public async Task InitializeAsync_Completes()
    {
        // Act & Assert - should not throw
        await _store.InitializeAsync(CancellationToken.None);
    }

    [Fact]
    public async Task QueryAsync_ReturnsEmptyList()
    {
        // Act
        var result = await _store.QueryAsync("session", "query", 10, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task QueryAsync_WithNullSessionId_ReturnsEmptyList()
    {
        // Act
        var result = await _store.QueryAsync(null, "query", null, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAllRecentAsync_ReturnsEmptyList()
    {
        // Act
        var result = await _store.GetAllRecentAsync(100, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task StoreAsync_Completes()
    {
        // Arrange
        var metadata = new Dictionary<string, string> { ["key"] = "value" };

        // Act & Assert - should not throw
        await _store.StoreAsync("session", "content", metadata, CancellationToken.None);
    }

    [Fact]
    public async Task StoreAsync_WithNullMetadata_Completes()
    {
        // Act & Assert - should not throw
        await _store.StoreAsync("session", "content", null, CancellationToken.None);
    }

    [Fact]
    public async Task DeleteSessionAsync_Completes()
    {
        // Act & Assert - should not throw
        await _store.DeleteSessionAsync("session-to-delete", CancellationToken.None);
    }
}

public class RagMemoryTests
{
    [Fact]
    public void RagMemory_Record_HasExpectedProperties()
    {
        // Arrange
        var metadata = new Dictionary<string, string> { ["key"] = "value" };
        var timestamp = DateTimeOffset.UtcNow;

        // Act
        var memory = new RagMemory(
            SessionId: "session-123",
            Content: "Test content",
            Timestamp: timestamp,
            Score: 0.95,
            Metadata: metadata
        );

        // Assert
        Assert.Equal("session-123", memory.SessionId);
        Assert.Equal("Test content", memory.Content);
        Assert.Equal(timestamp, memory.Timestamp);
        Assert.Equal(0.95, memory.Score);
        Assert.NotNull(memory.Metadata);
        Assert.Equal("value", memory.Metadata["key"]);
    }

    [Fact]
    public void RagMemory_WithNullMetadata_Works()
    {
        // Act
        var memory = new RagMemory(
            SessionId: "session",
            Content: "content",
            Timestamp: DateTimeOffset.UtcNow,
            Score: 0.5,
            Metadata: null
        );

        // Assert
        Assert.Null(memory.Metadata);
    }

    [Fact]
    public void RagMemory_Equality_Works()
    {
        // Arrange
        var timestamp = DateTimeOffset.UtcNow;
        var memory1 = new RagMemory("session", "content", timestamp, 0.5, null);
        var memory2 = new RagMemory("session", "content", timestamp, 0.5, null);
        var memory3 = new RagMemory("different", "content", timestamp, 0.5, null);

        // Assert - records have value equality
        Assert.Equal(memory1, memory2);
        Assert.NotEqual(memory1, memory3);
    }
}
