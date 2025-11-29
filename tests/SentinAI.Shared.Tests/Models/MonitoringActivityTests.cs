using SentinAI.Shared.Models;

namespace SentinAI.Shared.Tests.Models;

public class MonitoringActivityTests
{
    [Fact]
    public void DefaultConstructor_GeneratesId()
    {
        // Arrange & Act
        var activity = new MonitoringActivity();

        // Assert
        Assert.False(string.IsNullOrEmpty(activity.Id));
        Assert.True(Guid.TryParse(activity.Id, out _));
    }

    [Fact]
    public void DefaultValues_AreCorrectlySet()
    {
        // Arrange & Act
        var activity = new MonitoringActivity();

        // Assert
        Assert.Equal(MonitoringActivityType.FileChange, activity.Type);
        Assert.Equal(string.Empty, activity.Scope);
        Assert.Equal(string.Empty, activity.Drive);
        Assert.Equal(string.Empty, activity.State);
        Assert.Equal(string.Empty, activity.Message);
        Assert.NotEqual(default, activity.Timestamp);
        Assert.Null(activity.Metadata);
    }

    [Fact]
    public void AllProperties_CanBeSet()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var metadata = new Dictionary<string, string> { ["key"] = "value" };

        var activity = new MonitoringActivity
        {
            Id = "custom-id",
            Type = MonitoringActivityType.AnalysisComplete,
            Scope = @"C:\Windows\Temp",
            Drive = "C:",
            State = "Completed",
            Message = "Analysis finished",
            Timestamp = now,
            Metadata = metadata
        };

        // Assert
        Assert.Equal("custom-id", activity.Id);
        Assert.Equal(MonitoringActivityType.AnalysisComplete, activity.Type);
        Assert.Equal(@"C:\Windows\Temp", activity.Scope);
        Assert.Equal("C:", activity.Drive);
        Assert.Equal("Completed", activity.State);
        Assert.Equal("Analysis finished", activity.Message);
        Assert.Equal(now, activity.Timestamp);
        Assert.NotNull(activity.Metadata);
        Assert.Equal("value", activity.Metadata["key"]);
    }

    [Theory]
    [InlineData(MonitoringActivityType.FileChange)]
    [InlineData(MonitoringActivityType.AnalysisStart)]
    [InlineData(MonitoringActivityType.AnalysisComplete)]
    [InlineData(MonitoringActivityType.CleanupExecuted)]
    [InlineData(MonitoringActivityType.Error)]
    [InlineData(MonitoringActivityType.ServiceStatus)]
    [InlineData(MonitoringActivityType.Custom)]
    public void Type_AcceptsAllEnumValues(MonitoringActivityType type)
    {
        // Arrange
        var activity = new MonitoringActivity { Type = type };

        // Assert
        Assert.Equal(type, activity.Type);
    }
}

public class MonitoringActivityTypeTests
{
    [Fact]
    public void AllEnumValues_AreDefined()
    {
        // Assert that all expected values exist
        Assert.True(Enum.IsDefined(typeof(MonitoringActivityType), MonitoringActivityType.FileChange));
        Assert.True(Enum.IsDefined(typeof(MonitoringActivityType), MonitoringActivityType.AnalysisStart));
        Assert.True(Enum.IsDefined(typeof(MonitoringActivityType), MonitoringActivityType.AnalysisComplete));
        Assert.True(Enum.IsDefined(typeof(MonitoringActivityType), MonitoringActivityType.CleanupExecuted));
        Assert.True(Enum.IsDefined(typeof(MonitoringActivityType), MonitoringActivityType.Error));
        Assert.True(Enum.IsDefined(typeof(MonitoringActivityType), MonitoringActivityType.ServiceStatus));
        Assert.True(Enum.IsDefined(typeof(MonitoringActivityType), MonitoringActivityType.Custom));
    }

    [Fact]
    public void EnumValues_HaveExpectedCount()
    {
        var values = Enum.GetValues(typeof(MonitoringActivityType));
        Assert.Equal(7, values.Length);
    }
}
