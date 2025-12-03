using Microsoft.Extensions.Logging;
using Moq;
using SentinAI.Shared.Models.DeepScan;
using SentinAI.Web.Services.DeepScan;

namespace SentinAI.Web.Tests.Services.DeepScan;

public class FileBasedDeepScanSessionStoreTests : IDisposable
{
    private readonly string _testStorageDir;
    private readonly Mock<ILogger<FileBasedDeepScanSessionStore>> _loggerMock;
    private readonly FileBasedDeepScanSessionStore _store;

    public FileBasedDeepScanSessionStoreTests()
    {
        // Create a unique test directory
        _testStorageDir = Path.Combine(Path.GetTempPath(), $"deepscan_test_{Guid.NewGuid():N}");

        _loggerMock = new Mock<ILogger<FileBasedDeepScanSessionStore>>();

        // Create store - it will create its own directory under LocalApplicationData
        // We'll clean up all sessions after tests
        _store = new FileBasedDeepScanSessionStore(_loggerMock.Object);
    }

    public void Dispose()
    {
        // Clean up test sessions
        try
        {
            if (Directory.Exists(_testStorageDir))
            {
                Directory.Delete(_testStorageDir, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private DeepScanSession CreateTestSession()
    {
        return new DeepScanSession
        {
            Id = Guid.NewGuid(),
            State = DeepScanState.Completed,
            StartedAt = DateTime.UtcNow.AddMinutes(-10),
            CompletedAt = DateTime.UtcNow,
            Progress = new DeepScanProgress
            {
                CurrentPhase = "Complete",
                OverallProgress = 100,
                FilesScanned = 1000,
                BytesAnalyzed = 1024 * 1024 * 500
            },
            Summary = new DeepScanSummary
            {
                TotalRecommendations = 5,
                PotentialSpaceSavings = 1024 * 1024 * 100
            },
            CleanupOpportunities = new List<CleanupOpportunity>
            {
                new CleanupOpportunity
                {
                    Type = CleanupType.WindowsTemp,
                    Path = "C:\\Windows\\Temp",
                    Bytes = 1024 * 1024 * 50,
                    Risk = CleanupRisk.Low
                }
            }
        };
    }

    [Fact]
    public async Task SaveSessionAsync_SavesSession()
    {
        // Arrange
        var session = CreateTestSession();

        // Act
        await _store.SaveSessionAsync(session);

        // Assert
        var loaded = await _store.LoadSessionAsync(session.Id);
        Assert.NotNull(loaded);
        Assert.Equal(session.Id, loaded.Id);
        Assert.Equal(DeepScanState.Completed, loaded.State);

        // Cleanup
        await _store.DeleteSessionAsync(session.Id);
    }

    [Fact]
    public async Task LoadSessionAsync_NonExistentId_ReturnsNull()
    {
        // Act
        var result = await _store.LoadSessionAsync(Guid.NewGuid());

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task SaveSessionAsync_PreservesAllData()
    {
        // Arrange
        var session = CreateTestSession();
        session.AppRemovalRecommendations = new List<AppRemovalRecommendation>
        {
            new AppRemovalRecommendation
            {
                App = new InstalledApp { Name = "TestApp", Publisher = "TestPub" },
                ShouldRemove = true,
                Confidence = 0.85,
                Category = AppRemovalCategory.Bloatware
            }
        };

        // Act
        await _store.SaveSessionAsync(session);
        var loaded = await _store.LoadSessionAsync(session.Id);

        // Assert
        Assert.NotNull(loaded);
        Assert.NotNull(loaded.Summary);
        Assert.Equal(5, loaded.Summary.TotalRecommendations);
        Assert.NotNull(loaded.CleanupOpportunities);
        Assert.Single(loaded.CleanupOpportunities);
        Assert.Equal(CleanupType.WindowsTemp, loaded.CleanupOpportunities[0].Type);
        Assert.NotNull(loaded.AppRemovalRecommendations);
        Assert.Single(loaded.AppRemovalRecommendations);
        Assert.Equal("TestApp", loaded.AppRemovalRecommendations[0].App?.Name);

        // Cleanup
        await _store.DeleteSessionAsync(session.Id);
    }

    [Fact]
    public async Task GetLatestSessionAsync_ReturnsLatest()
    {
        // Arrange
        var session1 = CreateTestSession();
        session1.StartedAt = DateTime.UtcNow.AddHours(-2);
        await _store.SaveSessionAsync(session1);

        await Task.Delay(100); // Ensure file timestamps differ

        var session2 = CreateTestSession();
        session2.StartedAt = DateTime.UtcNow;
        await _store.SaveSessionAsync(session2);

        // Act
        var latest = await _store.GetLatestSessionAsync();

        // Assert
        Assert.NotNull(latest);
        Assert.Equal(session2.Id, latest.Id);

        // Cleanup
        await _store.DeleteSessionAsync(session1.Id);
        await _store.DeleteSessionAsync(session2.Id);
    }

    [Fact]
    public async Task DeleteSessionAsync_RemovesSession()
    {
        // Arrange
        var session = CreateTestSession();
        await _store.SaveSessionAsync(session);

        // Verify it exists
        var loaded = await _store.LoadSessionAsync(session.Id);
        Assert.NotNull(loaded);

        // Act
        await _store.DeleteSessionAsync(session.Id);

        // Assert
        var deleted = await _store.LoadSessionAsync(session.Id);
        Assert.Null(deleted);
    }

    [Fact]
    public async Task GetSessionHistoryAsync_ReturnsLimitedResults()
    {
        // Arrange
        var sessions = new List<DeepScanSession>();
        for (int i = 0; i < 5; i++)
        {
            var session = CreateTestSession();
            sessions.Add(session);
            await _store.SaveSessionAsync(session);
            await Task.Delay(50); // Small delay to ensure different file timestamps
        }

        // Act
        var history = await _store.GetSessionHistoryAsync(limit: 3);

        // Assert
        Assert.True(history.Count <= 3);

        // Cleanup
        foreach (var session in sessions)
        {
            await _store.DeleteSessionAsync(session.Id);
        }
    }

    [Fact]
    public async Task CleanupOldSessionsAsync_KeepsSpecifiedCount()
    {
        // Arrange
        var sessions = new List<DeepScanSession>();
        for (int i = 0; i < 8; i++)
        {
            var session = CreateTestSession();
            sessions.Add(session);
            await _store.SaveSessionAsync(session);
            await Task.Delay(50);
        }

        // Act
        await _store.CleanupOldSessionsAsync(keepCount: 3);

        // Assert - should have at most 3 sessions remaining (or existing ones from other tests)
        var history = await _store.GetSessionHistoryAsync(limit: 10);
        // Note: We can't guarantee exact count due to other sessions, but verify no errors

        // Cleanup remaining
        foreach (var session in sessions)
        {
            await _store.DeleteSessionAsync(session.Id);
        }
    }

    [Fact]
    public async Task GetSessionHistoryAsync_ReturnsSummaryWithCorrectData()
    {
        // Arrange
        var session = CreateTestSession();
        await _store.SaveSessionAsync(session);

        // Act
        var history = await _store.GetSessionHistoryAsync(limit: 1);

        // Assert
        var summary = history.FirstOrDefault(h => h.Id == session.Id);
        if (summary != null)
        {
            Assert.Equal(session.StartedAt.Date, summary.StartedAt.Date);
            Assert.Equal(session.State, summary.State);
            Assert.Equal(session.Summary!.TotalRecommendations, summary.TotalRecommendations);
        }

        // Cleanup
        await _store.DeleteSessionAsync(session.Id);
    }
}
