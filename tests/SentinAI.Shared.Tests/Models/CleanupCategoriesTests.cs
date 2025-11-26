using SentinAI.Shared.Models;

namespace SentinAI.Shared.Tests.Models;

public class CleanupCategoriesTests
{
    [Fact]
    public void AllCategories_HaveExpectedValues()
    {
        Assert.Equal("Temp", CleanupCategories.Temp);
        Assert.Equal("Cache", CleanupCategories.Cache);
        Assert.Equal("Downloads", CleanupCategories.Downloads);
        Assert.Equal("NodeModules", CleanupCategories.NodeModules);
        Assert.Equal("BuildArtifacts", CleanupCategories.BuildArtifacts);
        Assert.Equal("Logs", CleanupCategories.Logs);
        Assert.Equal("Unknown", CleanupCategories.Unknown);
    }

    [Fact]
    public void Categories_AreUnique()
    {
        var categories = new[]
        {
            CleanupCategories.Temp,
            CleanupCategories.Cache,
            CleanupCategories.Downloads,
            CleanupCategories.NodeModules,
            CleanupCategories.BuildArtifacts,
            CleanupCategories.Logs,
            CleanupCategories.Unknown
        };

        Assert.Equal(categories.Length, categories.Distinct().Count());
    }

    [Fact]
    public void Categories_AreNotNullOrEmpty()
    {
        Assert.False(string.IsNullOrEmpty(CleanupCategories.Temp));
        Assert.False(string.IsNullOrEmpty(CleanupCategories.Cache));
        Assert.False(string.IsNullOrEmpty(CleanupCategories.Downloads));
        Assert.False(string.IsNullOrEmpty(CleanupCategories.NodeModules));
        Assert.False(string.IsNullOrEmpty(CleanupCategories.BuildArtifacts));
        Assert.False(string.IsNullOrEmpty(CleanupCategories.Logs));
        Assert.False(string.IsNullOrEmpty(CleanupCategories.Unknown));
    }
}
