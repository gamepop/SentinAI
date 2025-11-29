using SentinAI.Shared.Models;

namespace SentinAI.Shared.Tests.Models;

public class CleanupSuggestionTests
{
    [Fact]
    public void DefaultValues_AreCorrectlySet()
    {
        // Arrange & Act
        var suggestion = new CleanupSuggestion();

        // Assert
        Assert.Equal(string.Empty, suggestion.FilePath);
        Assert.Equal(0, suggestion.SizeBytes);
        Assert.Null(suggestion.Category);
        Assert.False(suggestion.SafeToDelete);
        Assert.Null(suggestion.Reason);
        Assert.Equal(0.0, suggestion.Confidence);
        Assert.False(suggestion.AutoApprove);
    }

    [Fact]
    public void AllProperties_CanBeSet()
    {
        // Arrange
        var suggestion = new CleanupSuggestion
        {
            FilePath = @"C:\Windows\Temp\test.tmp",
            SizeBytes = 1024,
            Category = CleanupCategories.Temp,
            SafeToDelete = true,
            Reason = "Test reason",
            Confidence = 0.95,
            AutoApprove = true
        };

        // Assert
        Assert.Equal(@"C:\Windows\Temp\test.tmp", suggestion.FilePath);
        Assert.Equal(1024, suggestion.SizeBytes);
        Assert.Equal(CleanupCategories.Temp, suggestion.Category);
        Assert.True(suggestion.SafeToDelete);
        Assert.Equal("Test reason", suggestion.Reason);
        Assert.Equal(0.95, suggestion.Confidence);
        Assert.True(suggestion.AutoApprove);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Confidence_AcceptsValidRange(double confidence)
    {
        // Arrange
        var suggestion = new CleanupSuggestion { Confidence = confidence };

        // Assert
        Assert.Equal(confidence, suggestion.Confidence);
    }
}
