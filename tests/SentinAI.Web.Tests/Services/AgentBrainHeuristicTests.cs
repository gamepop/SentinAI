using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SentinAI.Shared.Models;
using SentinAI.Web.Services;
using SentinAI.Web.Services.Rag;

namespace SentinAI.Web.Tests.Services;

/// <summary>
/// Tests for AgentBrain's heuristic analysis functionality.
/// These tests focus on the rule-based analysis that doesn't require the AI model.
/// </summary>
public class AgentBrainHeuristicTests
{
    private readonly Mock<ILogger<AgentBrain>> _loggerMock;
    private readonly Mock<IRagStore> _ragStoreMock;
    private readonly BrainConfiguration _config;
    private readonly AgentBrain _brain;

    public AgentBrainHeuristicTests()
    {
        _loggerMock = new Mock<ILogger<AgentBrain>>();
        _ragStoreMock = new Mock<IRagStore>();
        _ragStoreMock.Setup(x => x.IsEnabled).Returns(false);

        _config = new BrainConfiguration();
        var options = Options.Create(_config);

        _brain = new AgentBrain(_loggerMock.Object, options, _ragStoreMock.Object);
    }

    [Fact]
    public async Task AnalyzeFolderAsync_WindowsTemp_ReturnsSafeToDelete()
    {
        // Arrange
        await _brain.InitializeAsync("/nonexistent/path");
        var folderPath = @"C:\Windows\Temp";
        var fileNames = new List<string> { "test.tmp", "cache.dat" };

        // Act
        var result = await _brain.AnalyzeFolderAsync(folderPath, fileNames);

        // Assert
        Assert.True(result.SafeToDelete);
        Assert.Equal(CleanupCategories.Temp, result.Category);
        Assert.True(result.Confidence >= 0.9);
        Assert.True(result.AutoApprove);
    }

    [Fact]
    public async Task AnalyzeFolderAsync_UserTemp_ReturnsSafeToDelete()
    {
        // Arrange
        await _brain.InitializeAsync("/nonexistent/path");
        var folderPath = @"C:\Users\TestUser\AppData\Local\Temp";
        var fileNames = new List<string> { "temp123.tmp" };

        // Act
        var result = await _brain.AnalyzeFolderAsync(folderPath, fileNames);

        // Assert
        Assert.True(result.SafeToDelete);
        Assert.Equal(CleanupCategories.Temp, result.Category);
        Assert.True(result.Confidence >= 0.9);
    }

    [Theory]
    [InlineData(@"C:\Users\Test\AppData\Local\Google\Chrome\User Data\Default\Cache")]
    [InlineData(@"C:\Users\Test\AppData\Local\Microsoft\Edge\User Data\Default\Cache2")]
    [InlineData(@"C:\Users\Test\AppData\Local\Mozilla\Firefox\Profiles\abc123\cache2")]
    [InlineData(@"C:\Users\Test\AppData\Local\BraveSoftware\Brave-Browser\User Data\Default\Cache")]
    public async Task AnalyzeFolderAsync_BrowserCache_ReturnsSafeToDelete(string folderPath)
    {
        // Arrange
        await _brain.InitializeAsync("/nonexistent/path");
        var fileNames = new List<string> { "data_0", "data_1", "index" };

        // Act
        var result = await _brain.AnalyzeFolderAsync(folderPath, fileNames);

        // Assert
        Assert.True(result.SafeToDelete);
        Assert.Equal(CleanupCategories.Cache, result.Category);
        Assert.True(result.AutoApprove);
    }

    [Fact]
    public async Task AnalyzeFolderAsync_NodeModules_ReturnsSafeToDelete()
    {
        // Arrange
        await _brain.InitializeAsync("/nonexistent/path");
        var folderPath = @"C:\Projects\MyApp\node_modules";
        var fileNames = new List<string> { "package.json", "index.js" };

        // Act
        var result = await _brain.AnalyzeFolderAsync(folderPath, fileNames);

        // Assert
        Assert.True(result.SafeToDelete);
        Assert.Equal(CleanupCategories.NodeModules, result.Category);
        Assert.False(result.AutoApprove); // Developer should confirm
    }

    [Theory]
    [InlineData(@"C:\Projects\MyApp\bin\Debug\net8.0")]
    [InlineData(@"C:\Projects\MyApp\obj\Release")]
    public async Task AnalyzeFolderAsync_BuildArtifacts_ReturnsSafeToDelete(string folderPath)
    {
        // Arrange
        await _brain.InitializeAsync("/nonexistent/path");
        var fileNames = new List<string> { "MyApp.dll", "MyApp.pdb" };

        // Act
        var result = await _brain.AnalyzeFolderAsync(folderPath, fileNames);

        // Assert
        Assert.True(result.SafeToDelete);
        Assert.Equal(CleanupCategories.BuildArtifacts, result.Category);
        Assert.False(result.AutoApprove); // Developer should confirm
    }

    [Fact]
    public async Task AnalyzeFolderAsync_GenericTempFolder_ReturnsSafeWithLowerConfidence()
    {
        // Arrange
        await _brain.InitializeAsync("/nonexistent/path");
        var folderPath = @"C:\SomeApp\TempData";
        var fileNames = new List<string> { "data.tmp", "session.tmp" };

        // Act
        var result = await _brain.AnalyzeFolderAsync(folderPath, fileNames);

        // Assert
        Assert.True(result.SafeToDelete);
        Assert.Equal(CleanupCategories.Temp, result.Category);
        Assert.False(result.AutoApprove); // Lower confidence = no auto-approve
    }

    [Fact]
    public async Task AnalyzeFolderAsync_GenericCacheFolder_ReturnsSafeWithLowerConfidence()
    {
        // Arrange
        await _brain.InitializeAsync("/nonexistent/path");
        var folderPath = @"C:\SomeApp\CacheData";
        var fileNames = new List<string> { "cache.dat", "cache_index" };

        // Act
        var result = await _brain.AnalyzeFolderAsync(folderPath, fileNames);

        // Assert
        Assert.True(result.SafeToDelete);
        Assert.Equal(CleanupCategories.Cache, result.Category);
    }

    [Fact]
    public async Task AnalyzeFolderAsync_LogFiles_ReturnsSafeWithModerateConfidence()
    {
        // Arrange
        await _brain.InitializeAsync("/nonexistent/path");
        var folderPath = @"C:\SomeApp\Logs";
        var fileNames = new List<string> { "app.log", "error.log", "debug.log" };

        // Act
        var result = await _brain.AnalyzeFolderAsync(folderPath, fileNames);

        // Assert
        Assert.True(result.SafeToDelete);
        Assert.Equal(CleanupCategories.Logs, result.Category);
        Assert.False(result.AutoApprove);
    }

    [Fact]
    public async Task AnalyzeFolderAsync_DownloadsWithImportantFiles_ReturnsUnsafe()
    {
        // Arrange
        await _brain.InitializeAsync("/nonexistent/path");
        var folderPath = @"C:\Users\Test\Downloads";
        var fileNames = new List<string> { "document.pdf", "photo.jpg", "report.xlsx" };

        // Act
        var result = await _brain.AnalyzeFolderAsync(folderPath, fileNames);

        // Assert
        Assert.False(result.SafeToDelete);
        Assert.Equal(CleanupCategories.Downloads, result.Category);
        Assert.Contains("documents", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeFolderAsync_DownloadsWithoutImportantFiles_ReturnsUnsafe()
    {
        // Arrange
        await _brain.InitializeAsync("/nonexistent/path");
        var folderPath = @"C:\Users\Test\Downloads";
        var fileNames = new List<string> { "setup.exe", "installer.msi" };

        // Act
        var result = await _brain.AnalyzeFolderAsync(folderPath, fileNames);

        // Assert
        Assert.False(result.SafeToDelete); // Downloads always need review
        Assert.Equal(CleanupCategories.Downloads, result.Category);
        Assert.False(result.AutoApprove);
    }

    [Fact]
    public async Task AnalyzeFolderAsync_UnknownFolder_ReturnsUnsafe()
    {
        // Arrange
        await _brain.InitializeAsync("/nonexistent/path");
        var folderPath = @"C:\SomeRandomFolder";
        var fileNames = new List<string> { "unknown.dat", "data.bin" };

        // Act
        var result = await _brain.AnalyzeFolderAsync(folderPath, fileNames);

        // Assert
        Assert.False(result.SafeToDelete);
        Assert.Equal(CleanupCategories.Unknown, result.Category);
        Assert.True(result.Confidence < 0.5); // Low confidence
        Assert.False(result.AutoApprove);
    }

    [Fact]
    public async Task AnalyzeFilesAsync_GroupsByDirectory()
    {
        // Arrange
        await _brain.InitializeAsync("/nonexistent/path");
        var filePaths = new List<string>
        {
            @"C:\Windows\Temp\file1.tmp",
            @"C:\Windows\Temp\file2.tmp",
            @"C:\Users\Test\AppData\Local\Temp\file3.tmp"
        };

        // Act
        var results = await _brain.AnalyzeFilesAsync(filePaths);

        // Assert
        Assert.Equal(2, results.Count); // Two directories
        Assert.All(results, r => Assert.True(r.SafeToDelete));
    }

    [Fact]
    public async Task GetStats_ReturnsCorrectCounts()
    {
        // Arrange
        await _brain.InitializeAsync("/nonexistent/path");

        // Act - perform some analyses
        await _brain.AnalyzeFolderAsync(@"C:\Windows\Temp", new List<string> { "a.tmp" });
        await _brain.AnalyzeFolderAsync(@"C:\Users\Test\AppData\Local\Temp", new List<string> { "b.tmp" });

        var stats = _brain.GetStats();

        // Assert
        Assert.Equal(2, stats.TotalAnalyses);
        Assert.Equal(2, stats.HeuristicOnly); // No model loaded
        Assert.Equal(0, stats.ModelDecisions);
        Assert.Equal(2, stats.SafeToDeleteCount);
    }

    [Fact]
    public void IsReady_ReturnsFalseBeforeInitialization()
    {
        Assert.False(_brain.IsReady);
    }

    [Fact]
    public async Task IsReady_ReturnsTrueAfterInitialization()
    {
        // Act
        await _brain.InitializeAsync("/nonexistent/path");

        // Assert
        Assert.True(_brain.IsReady);
    }

    [Fact]
    public async Task IsModelLoaded_ReturnsFalseForNonexistentPath()
    {
        // Act
        await _brain.InitializeAsync("/nonexistent/path");

        // Assert
        Assert.False(_brain.IsModelLoaded);
    }

    [Fact]
    public void ExecutionProvider_ReturnsConfiguredProvider()
    {
        // Assert
        Assert.Equal("CPU", _brain.ExecutionProvider);
    }

    [Fact]
    public async Task AnalyzeFolderAsync_CancellationToken_ThrowsOnCancellation()
    {
        // Arrange
        await _brain.InitializeAsync("/nonexistent/path");
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _brain.AnalyzeFolderAsync(@"C:\Windows\Temp", new List<string>(), cancellationToken: cts.Token));
    }

    [Fact]
    public async Task AnalyzeFolderAsync_CaseInsensitivePaths()
    {
        // Arrange
        await _brain.InitializeAsync("/nonexistent/path");

        // Act
        var result1 = await _brain.AnalyzeFolderAsync(@"C:\WINDOWS\TEMP", new List<string>());
        var result2 = await _brain.AnalyzeFolderAsync(@"c:\windows\temp", new List<string>());

        // Assert
        Assert.Equal(result1.Category, result2.Category);
        Assert.Equal(result1.SafeToDelete, result2.SafeToDelete);
    }

    [Fact]
    public void Dispose_CleansUpResources()
    {
        // Act
        _brain.Dispose();

        // Assert
        Assert.False(_brain.IsReady);
        Assert.False(_brain.IsModelLoaded);
    }
}
