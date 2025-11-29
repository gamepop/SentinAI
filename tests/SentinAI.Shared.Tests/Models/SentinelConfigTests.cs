using SentinAI.Shared.Models;

namespace SentinAI.Shared.Tests.Models;

public class SentinelConfigTests
{
    [Fact]
    public void DefaultValues_AreCorrectlySet()
    {
        // Arrange & Act
        var config = new SentinelConfig();

        // Assert
        Assert.False(config.AutoStart);
        Assert.False(config.StartMinimized);
        Assert.True(config.ShowNotifications);
        Assert.Equal(0.95, config.AutoApproveMinConfidence);
        Assert.False(config.EnableAutoCleanup);
        Assert.NotNull(config.ExcludedPaths);
        Assert.Empty(config.ExcludedPaths);
        Assert.True(config.RagEnabled);
        Assert.Equal("CPU", config.ExecutionProvider);
    }

    [Fact]
    public void DefaultAutoCleanupCategories_ContainsTempAndCache()
    {
        // Arrange & Act
        var config = new SentinelConfig();

        // Assert
        Assert.NotNull(config.AutoCleanupCategories);
        Assert.Contains(CleanupCategories.Temp, config.AutoCleanupCategories);
        Assert.Contains(CleanupCategories.Cache, config.AutoCleanupCategories);
        Assert.Equal(2, config.AutoCleanupCategories.Count);
    }

    [Fact]
    public void AllProperties_CanBeSet()
    {
        // Arrange
        var config = new SentinelConfig
        {
            AutoStart = true,
            StartMinimized = true,
            ShowNotifications = false,
            AutoApproveMinConfidence = 0.8,
            EnableAutoCleanup = true,
            ExcludedPaths = new List<string> { @"C:\Important" },
            AutoCleanupCategories = new List<string> { CleanupCategories.Logs },
            RagEnabled = false,
            ExecutionProvider = "DirectML"
        };

        // Assert
        Assert.True(config.AutoStart);
        Assert.True(config.StartMinimized);
        Assert.False(config.ShowNotifications);
        Assert.Equal(0.8, config.AutoApproveMinConfidence);
        Assert.True(config.EnableAutoCleanup);
        Assert.Single(config.ExcludedPaths);
        Assert.Contains(@"C:\Important", config.ExcludedPaths);
        Assert.Single(config.AutoCleanupCategories);
        Assert.Contains(CleanupCategories.Logs, config.AutoCleanupCategories);
        Assert.False(config.RagEnabled);
        Assert.Equal("DirectML", config.ExecutionProvider);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(0.9)]
    [InlineData(1.0)]
    public void AutoApproveMinConfidence_AcceptsValidRange(double confidence)
    {
        // Arrange
        var config = new SentinelConfig { AutoApproveMinConfidence = confidence };

        // Assert
        Assert.Equal(confidence, config.AutoApproveMinConfidence);
    }

    [Theory]
    [InlineData("CPU")]
    [InlineData("DirectML")]
    [InlineData("GPU")]
    public void ExecutionProvider_AcceptsValidValues(string provider)
    {
        // Arrange
        var config = new SentinelConfig { ExecutionProvider = provider };

        // Assert
        Assert.Equal(provider, config.ExecutionProvider);
    }
}
