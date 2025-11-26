using SentinAI.Shared.Models;

namespace SentinAI.Shared.Tests.Models;

public class BrainSessionContextTests
{
    [Fact]
    public void DefaultConstructor_GeneratesSessionId()
    {
        // Arrange & Act
        var context = new BrainSessionContext();

        // Assert
        Assert.False(string.IsNullOrEmpty(context.SessionId));
        Assert.True(Guid.TryParse(context.SessionId, out _));
    }

    [Fact]
    public void DefaultConstructor_QueryHintIsNull()
    {
        // Arrange & Act
        var context = new BrainSessionContext();

        // Assert
        Assert.Null(context.QueryHint);
    }

    [Fact]
    public void Create_WithNoArgs_ReturnsNewContext()
    {
        // Arrange & Act
        var context = BrainSessionContext.Create();

        // Assert
        Assert.NotNull(context);
        Assert.False(string.IsNullOrEmpty(context.SessionId));
        Assert.Null(context.QueryHint);
    }

    [Fact]
    public void Create_WithQueryHint_SetsQueryHint()
    {
        // Arrange
        const string hint = "Find large temp files";

        // Act
        var context = BrainSessionContext.Create(hint);

        // Assert
        Assert.Equal(hint, context.QueryHint);
    }

    [Fact]
    public void Create_MultipleCalls_GenerateUniqueSessionIds()
    {
        // Arrange & Act
        var context1 = BrainSessionContext.Create();
        var context2 = BrainSessionContext.Create();

        // Assert
        Assert.NotEqual(context1.SessionId, context2.SessionId);
    }

    [Fact]
    public void SessionId_CanBeSet()
    {
        // Arrange
        var context = new BrainSessionContext();
        const string customId = "custom-session-id";

        // Act
        context.SessionId = customId;

        // Assert
        Assert.Equal(customId, context.SessionId);
    }
}
