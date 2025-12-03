using Microsoft.Extensions.Logging;
using Moq;
using SentinAI.Shared.Models.DeepScan;
using SentinAI.Web.Services;
using SentinAI.Web.Services.DeepScan;
// Alias to avoid ambiguity with SentinAI.Web.Services.DuplicateGroup
using DeepScanDuplicateGroup = SentinAI.Shared.Models.DeepScan.DuplicateGroup;

namespace SentinAI.Web.Tests.Services.DeepScan;

public class DeepScanExecutionServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly Mock<ILogger<DeepScanExecutionService>> _loggerMock;
    private readonly Mock<IDuplicateFileService> _duplicateServiceMock;
    private readonly Mock<IDeepScanSessionStore> _sessionStoreMock;
    private readonly Mock<IDeepScanRagStore> _ragStoreMock;
    private readonly Mock<ILogger<DeepScanLearningService>> _learningLoggerMock;
    private readonly DeepScanLearningService _learningService;
    private readonly DeepScanExecutionService _service;

    public DeepScanExecutionServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"deepscan_exec_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);

        _loggerMock = new Mock<ILogger<DeepScanExecutionService>>();
        _duplicateServiceMock = new Mock<IDuplicateFileService>();
        _sessionStoreMock = new Mock<IDeepScanSessionStore>();
        _ragStoreMock = new Mock<IDeepScanRagStore>();
        _learningLoggerMock = new Mock<ILogger<DeepScanLearningService>>();

        _learningService = new DeepScanLearningService(_learningLoggerMock.Object, _ragStoreMock.Object);

        _service = new DeepScanExecutionService(
            _loggerMock.Object,
            _duplicateServiceMock.Object,
            _sessionStoreMock.Object,
            _learningService);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
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
            Summary = new DeepScanSummary
            {
                TotalRecommendations = 0,
                PotentialSpaceSavings = 0
            }
        };
    }

    #region ExecuteCleanupAsync Tests

    [Fact]
    public async Task ExecuteCleanupAsync_WithNoApprovedItems_ReturnsEmptyResult()
    {
        // Arrange
        var session = CreateTestSession();
        session.CleanupOpportunities = new List<CleanupOpportunity>
        {
            new CleanupOpportunity
            {
                Type = CleanupType.UserTemp,
                Path = "C:\\Temp",
                Status = RecommendationStatus.Pending // Not approved
            }
        };

        // Act
        var result = await _service.ExecuteCleanupAsync(session);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(0, result.ItemsProcessed);
        Assert.Equal(0, result.ItemsFailed);
    }

    [Fact]
    public async Task ExecuteCleanupAsync_WithApprovedDirectory_DeletesFiles()
    {
        // Arrange
        var testTempDir = Path.Combine(_testDir, "temp");
        Directory.CreateDirectory(testTempDir);
        var testFile = Path.Combine(testTempDir, "test.tmp");
        await File.WriteAllTextAsync(testFile, "test content");

        var session = CreateTestSession();
        session.CleanupOpportunities = new List<CleanupOpportunity>
        {
            new CleanupOpportunity
            {
                Type = CleanupType.UserTemp,
                Path = testTempDir,
                Status = RecommendationStatus.Approved,
                Bytes = 12
            }
        };

        // Act
        var result = await _service.ExecuteCleanupAsync(session);

        // Assert
        Assert.Equal(1, result.ItemsProcessed);
        Assert.True(result.BytesFreed > 0);
        Assert.False(File.Exists(testFile)); // File should be deleted
        Assert.True(Directory.Exists(testTempDir)); // Folder should remain for temp type
    }

    [Fact]
    public async Task ExecuteCleanupAsync_WithNonExistentPath_ReportsFailure()
    {
        // Arrange
        var session = CreateTestSession();
        session.CleanupOpportunities = new List<CleanupOpportunity>
        {
            new CleanupOpportunity
            {
                Type = CleanupType.UserTemp,
                Path = Path.Combine(_testDir, "nonexistent"),
                Status = RecommendationStatus.Approved
            }
        };

        // Act
        var result = await _service.ExecuteCleanupAsync(session);

        // Assert
        Assert.Equal(0, result.ItemsProcessed);
        Assert.Equal(1, result.ItemsFailed);
        Assert.Contains("Path not found", result.Errors[0]);
    }

    [Fact]
    public async Task ExecuteCleanupAsync_UpdatesSessionStore()
    {
        // Arrange
        var session = CreateTestSession();
        session.CleanupOpportunities = new List<CleanupOpportunity>();

        // Act
        await _service.ExecuteCleanupAsync(session);

        // Assert
        _sessionStoreMock.Verify(s => s.SaveSessionAsync(session), Times.Once);
    }

    [Fact]
    public async Task ExecuteCleanupAsync_ReportProgressCorrectly()
    {
        // Arrange
        var testTempDir = Path.Combine(_testDir, "temp_progress");
        Directory.CreateDirectory(testTempDir);
        await File.WriteAllTextAsync(Path.Combine(testTempDir, "test.tmp"), "test");

        var session = CreateTestSession();
        session.CleanupOpportunities = new List<CleanupOpportunity>
        {
            new CleanupOpportunity
            {
                Type = CleanupType.UserTemp,
                Path = testTempDir,
                Status = RecommendationStatus.Approved
            }
        };

        var progressReports = new List<ExecutionProgress>();
        var progress = new Progress<ExecutionProgress>(p => progressReports.Add(new ExecutionProgress
        {
            Phase = p.Phase,
            TotalItems = p.TotalItems,
            CompletedItems = p.CompletedItems,
            CurrentItem = p.CurrentItem
        }));

        // Act
        await _service.ExecuteCleanupAsync(session, progress);

        // Allow progress events to be processed
        await Task.Delay(100);

        // Assert
        Assert.NotEmpty(progressReports);
        Assert.Equal("Cleaning up files", progressReports.First().Phase);
    }

    #endregion

    #region ExecuteDuplicateRemovalAsync Tests

    [Fact]
    public async Task ExecuteDuplicateRemovalAsync_WithNoDuplicates_ReturnsEmptyResult()
    {
        // Arrange
        var session = CreateTestSession();
        session.DuplicateGroups = new List<DeepScanDuplicateGroup>();

        // Act
        var result = await _service.ExecuteDuplicateRemovalAsync(session);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(0, result.ItemsProcessed);
        Assert.Equal(0, result.BytesFreed);
    }

    [Fact]
    public async Task ExecuteDuplicateRemovalAsync_KeepsOldestFile()
    {
        // Arrange
        var oldFile = Path.Combine(_testDir, "old.txt");
        var newFile = Path.Combine(_testDir, "new.txt");

        await File.WriteAllTextAsync(oldFile, "duplicate content");
        await Task.Delay(100);
        await File.WriteAllTextAsync(newFile, "duplicate content");

        // Set old file's modified time to the past
        File.SetLastWriteTime(oldFile, DateTime.Now.AddDays(-1));

        var session = CreateTestSession();
        session.DuplicateGroups = new List<DeepScanDuplicateGroup>
        {
            new DeepScanDuplicateGroup
            {
                Hash = "abc123",
                Files = new List<DuplicateFileEntry>
                {
                    new DuplicateFileEntry { Path = oldFile, LastModified = DateTime.Now.AddDays(-1) },
                    new DuplicateFileEntry { Path = newFile, LastModified = DateTime.Now }
                }
            }
        };

        // Act
        var result = await _service.ExecuteDuplicateRemovalAsync(session);

        // Assert
        Assert.Equal(1, result.ItemsProcessed);
        Assert.True(result.BytesFreed > 0);
        Assert.True(File.Exists(oldFile), "Old file should be kept");
        Assert.False(File.Exists(newFile), "New file should be deleted");
    }

    [Fact]
    public async Task ExecuteDuplicateRemovalAsync_ClearsDuplicateGroupsFromSession()
    {
        // Arrange
        var session = CreateTestSession();
        session.DuplicateGroups = new List<DeepScanDuplicateGroup>
        {
            new DeepScanDuplicateGroup
            {
                Hash = "abc123",
                Files = new List<DuplicateFileEntry>()
            }
        };

        // Act
        await _service.ExecuteDuplicateRemovalAsync(session);

        // Assert
        Assert.Empty(session.DuplicateGroups);
    }

    [Fact]
    public async Task ExecuteDuplicateRemovalAsync_HandlesNonExistentFiles()
    {
        // Arrange
        var existingFile = Path.Combine(_testDir, "existing.txt");
        await File.WriteAllTextAsync(existingFile, "content");

        var session = CreateTestSession();
        session.DuplicateGroups = new List<DeepScanDuplicateGroup>
        {
            new DeepScanDuplicateGroup
            {
                Hash = "abc123",
                Files = new List<DuplicateFileEntry>
                {
                    new DuplicateFileEntry { Path = existingFile, LastModified = DateTime.Now.AddDays(-1) },
                    new DuplicateFileEntry { Path = Path.Combine(_testDir, "nonexistent.txt"), LastModified = DateTime.Now }
                }
            }
        };

        // Act
        var result = await _service.ExecuteDuplicateRemovalAsync(session);

        // Assert - should not throw, non-existent file just doesn't add to freed bytes
        Assert.True(result.Success || result.ItemsFailed == 0);
        Assert.True(File.Exists(existingFile), "Oldest file should be kept");
    }

    #endregion

    #region ApproveAllSafeAsync Tests

    [Fact]
    public async Task ApproveAllSafeAsync_ApprovesHighConfidenceItems()
    {
        // Arrange
        var session = CreateTestSession();
        session.AppRemovalRecommendations = new List<AppRemovalRecommendation>
        {
            new AppRemovalRecommendation
            {
                Status = RecommendationStatus.Pending,
                ShouldRemove = true,
                Confidence = 0.9
            },
            new AppRemovalRecommendation
            {
                Status = RecommendationStatus.Pending,
                ShouldRemove = true,
                Confidence = 0.5 // Below threshold
            }
        };

        // Act
        var approvedCount = await _service.ApproveAllSafeAsync(session, minConfidence: 0.8);

        // Assert
        Assert.Equal(1, approvedCount);
        Assert.Equal(RecommendationStatus.Approved, session.AppRemovalRecommendations[0].Status);
        Assert.Equal(RecommendationStatus.Pending, session.AppRemovalRecommendations[1].Status);
    }

    [Fact]
    public async Task ApproveAllSafeAsync_ApprovesLowRiskCleanups()
    {
        // Arrange
        var session = CreateTestSession();
        session.CleanupOpportunities = new List<CleanupOpportunity>
        {
            new CleanupOpportunity
            {
                Status = RecommendationStatus.Pending,
                Risk = CleanupRisk.Low,
                Confidence = 0.85
            },
            new CleanupOpportunity
            {
                Status = RecommendationStatus.Pending,
                Risk = CleanupRisk.High, // High risk, should not approve
                Confidence = 0.9
            }
        };

        // Act
        var approvedCount = await _service.ApproveAllSafeAsync(session, minConfidence: 0.8);

        // Assert
        Assert.Equal(1, approvedCount);
        Assert.Equal(RecommendationStatus.Approved, session.CleanupOpportunities[0].Status);
        Assert.Equal(RecommendationStatus.Pending, session.CleanupOpportunities[1].Status);
    }

    [Fact]
    public async Task ApproveAllSafeAsync_ApprovesRelocations()
    {
        // Arrange
        var session = CreateTestSession();
        session.RelocationRecommendations = new List<RelocationRecommendation>
        {
            new RelocationRecommendation
            {
                Status = RecommendationStatus.Pending,
                ShouldRelocate = true,
                Confidence = 0.85
            },
            new RelocationRecommendation
            {
                Status = RecommendationStatus.Pending,
                ShouldRelocate = false, // Not recommended
                Confidence = 0.9
            }
        };

        // Act
        var approvedCount = await _service.ApproveAllSafeAsync(session, minConfidence: 0.8);

        // Assert
        Assert.Equal(1, approvedCount);
    }

    [Fact]
    public async Task ApproveAllSafeAsync_SkipsAlreadyApprovedItems()
    {
        // Arrange
        var session = CreateTestSession();
        session.AppRemovalRecommendations = new List<AppRemovalRecommendation>
        {
            new AppRemovalRecommendation
            {
                Status = RecommendationStatus.Approved, // Already approved
                ShouldRemove = true,
                Confidence = 0.9
            }
        };

        // Act
        var approvedCount = await _service.ApproveAllSafeAsync(session);

        // Assert
        Assert.Equal(0, approvedCount);
    }

    [Fact]
    public async Task ApproveAllSafeAsync_SavesSessionAfterApproval()
    {
        // Arrange
        var session = CreateTestSession();
        session.AppRemovalRecommendations = new List<AppRemovalRecommendation>();

        // Act
        await _service.ApproveAllSafeAsync(session);

        // Assert
        _sessionStoreMock.Verify(s => s.SaveSessionAsync(session), Times.Once);
    }

    #endregion

    #region ExecuteAllApprovedAsync Tests

    [Fact]
    public async Task ExecuteAllApprovedAsync_ExecutesAllCategories()
    {
        // Arrange
        var tempDir = Path.Combine(_testDir, "temp_all");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "test.tmp"), "test");

        var session = CreateTestSession();
        session.CleanupOpportunities = new List<CleanupOpportunity>
        {
            new CleanupOpportunity
            {
                Type = CleanupType.UserTemp,
                Path = tempDir,
                Status = RecommendationStatus.Approved
            }
        };
        session.DuplicateGroups = new List<DeepScanDuplicateGroup>();
        session.AppRemovalRecommendations = new List<AppRemovalRecommendation>();
        session.RelocationRecommendations = new List<RelocationRecommendation>();

        // Act
        var result = await _service.ExecuteAllApprovedAsync(session);

        // Assert
        Assert.True(result.ItemsProcessed >= 1);
        Assert.Equal(DeepScanState.Completed, session.State);
    }

    [Fact]
    public async Task ExecuteAllApprovedAsync_ReturnsAggregatedResults()
    {
        // Arrange
        var session = CreateTestSession();
        session.CleanupOpportunities = new List<CleanupOpportunity>();
        session.DuplicateGroups = new List<DeepScanDuplicateGroup>();
        session.AppRemovalRecommendations = new List<AppRemovalRecommendation>();
        session.RelocationRecommendations = new List<RelocationRecommendation>();

        // Act
        var result = await _service.ExecuteAllApprovedAsync(session);

        // Assert
        Assert.True(result.Success);
        Assert.Contains("Completed", result.Message);
    }

    [Fact]
    public async Task ExecuteAllApprovedAsync_SetsSessionStateToCompleted()
    {
        // Arrange
        var session = CreateTestSession();
        session.State = DeepScanState.AnalyzingWithAi;
        session.CleanupOpportunities = new List<CleanupOpportunity>();
        session.DuplicateGroups = new List<DeepScanDuplicateGroup>();
        session.AppRemovalRecommendations = new List<AppRemovalRecommendation>();
        session.RelocationRecommendations = new List<RelocationRecommendation>();

        // Act
        await _service.ExecuteAllApprovedAsync(session);

        // Assert
        Assert.Equal(DeepScanState.Completed, session.State);
    }

    #endregion

    #region ExecuteRelocationAsync Tests

    [Fact]
    public async Task ExecuteRelocationAsync_WithNoApprovedItems_ReturnsEmptyResult()
    {
        // Arrange
        var session = CreateTestSession();
        session.RelocationRecommendations = new List<RelocationRecommendation>
        {
            new RelocationRecommendation
            {
                Status = RecommendationStatus.Pending,
                TargetDrive = "D:\\"
            }
        };

        // Act
        var result = await _service.ExecuteRelocationAsync(session);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(0, result.ItemsProcessed);
    }

    [Fact]
    public async Task ExecuteRelocationAsync_WithInvalidCluster_SkipsItem()
    {
        // Arrange
        var session = CreateTestSession();
        session.RelocationRecommendations = new List<RelocationRecommendation>
        {
            new RelocationRecommendation
            {
                Status = RecommendationStatus.Approved,
                Cluster = null, // No cluster
                TargetDrive = "D:\\"
            }
        };

        // Act
        var result = await _service.ExecuteRelocationAsync(session);

        // Assert
        Assert.Equal(0, result.ItemsProcessed);
        Assert.Equal(0, result.ItemsFailed);
    }

    [Fact]
    public async Task ExecuteRelocationAsync_WithNonExistentSourcePath_ReportsFailure()
    {
        // Arrange
        var session = CreateTestSession();
        session.RelocationRecommendations = new List<RelocationRecommendation>
        {
            new RelocationRecommendation
            {
                Status = RecommendationStatus.Approved,
                Cluster = new FileCluster
                {
                    Name = "Test Cluster",
                    BasePath = Path.Combine(_testDir, "nonexistent")
                },
                TargetDrive = _testDir // Use test dir as target
            }
        };

        // Act
        var result = await _service.ExecuteRelocationAsync(session);

        // Assert
        Assert.Equal(0, result.ItemsProcessed);
        Assert.Equal(1, result.ItemsFailed);
    }

    #endregion

    #region Cancellation Tests

    [Fact]
    public async Task ExecuteCleanupAsync_RespectsCancellation()
    {
        // Arrange
        var session = CreateTestSession();
        session.CleanupOpportunities = new List<CleanupOpportunity>
        {
            new CleanupOpportunity
            {
                Type = CleanupType.UserTemp,
                Path = _testDir,
                Status = RecommendationStatus.Approved
            }
        };

        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await _service.ExecuteCleanupAsync(session, ct: cts.Token));
    }

    [Fact]
    public async Task ExecuteDuplicateRemovalAsync_RespectsCancellation()
    {
        // Arrange
        var file1 = Path.Combine(_testDir, "dup1.txt");
        var file2 = Path.Combine(_testDir, "dup2.txt");
        await File.WriteAllTextAsync(file1, "content");
        await File.WriteAllTextAsync(file2, "content");

        var session = CreateTestSession();
        session.DuplicateGroups = new List<DeepScanDuplicateGroup>
        {
            new DeepScanDuplicateGroup
            {
                Hash = "abc",
                Files = new List<DuplicateFileEntry>
                {
                    new DuplicateFileEntry { Path = file1, LastModified = DateTime.Now.AddDays(-1) },
                    new DuplicateFileEntry { Path = file2, LastModified = DateTime.Now }
                }
            }
        };

        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await _service.ExecuteDuplicateRemovalAsync(session, ct: cts.Token));
    }

    #endregion
}
