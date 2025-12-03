using Microsoft.Extensions.Logging;
using Moq;
using SentinAI.Shared.Models.DeepScan;
using SentinAI.Web.Services.DeepScan;

namespace SentinAI.Web.Tests.Services.DeepScan;

public class DeepScanLearningServiceTests
{
    private readonly Mock<ILogger<DeepScanLearningService>> _loggerMock;
    private readonly Mock<IDeepScanRagStore> _ragStoreMock;
    private readonly DeepScanLearningService _service;

    public DeepScanLearningServiceTests()
    {
        _loggerMock = new Mock<ILogger<DeepScanLearningService>>();
        _ragStoreMock = new Mock<IDeepScanRagStore>();
        _service = new DeepScanLearningService(_loggerMock.Object, _ragStoreMock.Object);
    }

    #region RecordAppDecisionAsync Tests

    [Fact]
    public async Task RecordAppDecisionAsync_StoresMemoryWithCorrectType()
    {
        // Arrange
        var recommendation = new AppRemovalRecommendation
        {
            App = new InstalledApp
            {
                Name = "TestApp",
                Publisher = "TestPublisher",
                Category = AppCategory.Utility
            },
            Confidence = 0.85,
            AiReason = "Unused application"
        };

        DeepScanMemory? capturedMemory = null;
        _ragStoreMock.Setup(r => r.StoreMemoryAsync(It.IsAny<DeepScanMemory>()))
            .Callback<DeepScanMemory>(m => capturedMemory = m)
            .Returns(Task.CompletedTask);

        // Act
        await _service.RecordAppDecisionAsync(recommendation, userApproved: true);

        // Assert
        Assert.NotNull(capturedMemory);
        Assert.Equal(DeepScanMemoryType.AppRemovalDecision, capturedMemory.Type);
        Assert.Equal("approved", capturedMemory.Decision);
        Assert.True(capturedMemory.UserAgreed);
    }

    [Fact]
    public async Task RecordAppDecisionAsync_StoresRejectionCorrectly()
    {
        // Arrange
        var recommendation = new AppRemovalRecommendation
        {
            App = new InstalledApp { Name = "TestApp" },
            Confidence = 0.5
        };

        DeepScanMemory? capturedMemory = null;
        _ragStoreMock.Setup(r => r.StoreMemoryAsync(It.IsAny<DeepScanMemory>()))
            .Callback<DeepScanMemory>(m => capturedMemory = m)
            .Returns(Task.CompletedTask);

        // Act
        await _service.RecordAppDecisionAsync(recommendation, userApproved: false);

        // Assert
        Assert.NotNull(capturedMemory);
        Assert.Equal("rejected", capturedMemory.Decision);
        Assert.False(capturedMemory.UserAgreed);
    }

    [Fact]
    public async Task RecordAppDecisionAsync_StoresCorrectionForHighConfidenceRejection()
    {
        // Arrange
        var recommendation = new AppRemovalRecommendation
        {
            App = new InstalledApp { Name = "ImportantApp" },
            Confidence = 0.9, // High confidence
            AiReason = "Rarely used"
        };

        var storedMemories = new List<DeepScanMemory>();
        _ragStoreMock.Setup(r => r.StoreMemoryAsync(It.IsAny<DeepScanMemory>()))
            .Callback<DeepScanMemory>(m => storedMemories.Add(m))
            .Returns(Task.CompletedTask);

        // Act
        await _service.RecordAppDecisionAsync(recommendation, userApproved: false, "I need this app");

        // Assert
        Assert.Equal(2, storedMemories.Count);
        Assert.Contains(storedMemories, m => m.Type == DeepScanMemoryType.AppRemovalDecision);
        Assert.Contains(storedMemories, m => m.Type == DeepScanMemoryType.CorrectionPattern);
    }

    [Fact]
    public async Task RecordAppDecisionAsync_IncludesMetadata()
    {
        // Arrange
        var recommendation = new AppRemovalRecommendation
        {
            App = new InstalledApp
            {
                Name = "TestApp",
                Publisher = "Acme Corp",
                LastUsedDate = DateTime.Now.AddDays(-30),
                Category = AppCategory.Gaming
            },
            UninstallSavings = 1024 * 1024 * 100  // TotalPotentialSavings is computed from this
        };

        DeepScanMemory? capturedMemory = null;
        _ragStoreMock.Setup(r => r.StoreMemoryAsync(It.IsAny<DeepScanMemory>()))
            .Callback<DeepScanMemory>(m => capturedMemory = m)
            .Returns(Task.CompletedTask);

        // Act
        await _service.RecordAppDecisionAsync(recommendation, true);

        // Assert
        Assert.NotNull(capturedMemory?.Metadata);
        Assert.Equal("TestApp", capturedMemory.Metadata["appName"]);
        Assert.Equal("Acme Corp", capturedMemory.Metadata["publisher"]);
        Assert.Equal("30", capturedMemory.Metadata["daysSinceLastUse"]);
        Assert.Equal("Gaming", capturedMemory.Metadata["category"]);
    }

    #endregion

    #region RecordRelocationDecisionAsync Tests

    [Fact]
    public async Task RecordRelocationDecisionAsync_StoresMemoryWithCorrectType()
    {
        // Arrange
        var recommendation = new RelocationRecommendation
        {
            Cluster = new FileCluster
            {
                Name = "Videos",
                Type = FileClusterType.MediaVideos,
                BasePath = "C:\\Users\\Test\\Videos"
            },
            TargetDrive = "D:\\",
            Confidence = 0.8
        };

        DeepScanMemory? capturedMemory = null;
        _ragStoreMock.Setup(r => r.StoreMemoryAsync(It.IsAny<DeepScanMemory>()))
            .Callback<DeepScanMemory>(m => capturedMemory = m)
            .Returns(Task.CompletedTask);

        // Act
        await _service.RecordRelocationDecisionAsync(recommendation, userApproved: true);

        // Assert
        Assert.NotNull(capturedMemory);
        Assert.Equal(DeepScanMemoryType.RelocationDecision, capturedMemory.Type);
        Assert.Equal("approved", capturedMemory.Decision);
    }

    [Fact]
    public async Task RecordRelocationDecisionAsync_LearnsDrivePreference()
    {
        // Arrange
        var recommendation = new RelocationRecommendation
        {
            Cluster = new FileCluster { Type = FileClusterType.MediaVideos },
            TargetDrive = "D:\\" // Suggested
        };

        var storedMemories = new List<DeepScanMemory>();
        _ragStoreMock.Setup(r => r.StoreMemoryAsync(It.IsAny<DeepScanMemory>()))
            .Callback<DeepScanMemory>(m => storedMemories.Add(m))
            .Returns(Task.CompletedTask);

        // Act - User approves but chooses different drive
        await _service.RecordRelocationDecisionAsync(recommendation, userApproved: true, actualTargetDrive: "E:\\");

        // Assert
        Assert.Equal(2, storedMemories.Count);
        Assert.Contains(storedMemories, m => m.Type == DeepScanMemoryType.RelocationDecision);
        Assert.Contains(storedMemories, m => m.Type == DeepScanMemoryType.UserPreference);
    }

    [Fact]
    public async Task RecordRelocationDecisionAsync_IncludesClusterMetadata()
    {
        // Arrange
        var recommendation = new RelocationRecommendation
        {
            Cluster = new FileCluster
            {
                Name = "Documents",
                Type = FileClusterType.Documents,
                BasePath = "C:\\Users\\Test\\Documents",
                TotalBytes = 1024 * 1024 * 500,
                FileCount = 250
            },
            TargetDrive = "D:\\"
        };

        DeepScanMemory? capturedMemory = null;
        _ragStoreMock.Setup(r => r.StoreMemoryAsync(It.IsAny<DeepScanMemory>()))
            .Callback<DeepScanMemory>(m => capturedMemory = m)
            .Returns(Task.CompletedTask);

        // Act
        await _service.RecordRelocationDecisionAsync(recommendation, true);

        // Assert
        Assert.NotNull(capturedMemory?.Metadata);
        Assert.Equal("Documents", capturedMemory.Metadata["clusterType"]);
        Assert.Equal("Documents", capturedMemory.Metadata["clusterName"]);
        Assert.Equal("D:\\", capturedMemory.Metadata["targetDrive"]);
    }

    #endregion

    #region RecordCleanupDecisionAsync Tests

    [Fact]
    public async Task RecordCleanupDecisionAsync_StoresMemoryWithCorrectType()
    {
        // Arrange
        var opportunity = new CleanupOpportunity
        {
            Type = CleanupType.UserTemp,
            Path = "C:\\Users\\Test\\AppData\\Local\\Temp",
            Bytes = 1024 * 1024 * 100,
            Risk = CleanupRisk.Low
        };

        DeepScanMemory? capturedMemory = null;
        _ragStoreMock.Setup(r => r.StoreMemoryAsync(It.IsAny<DeepScanMemory>()))
            .Callback<DeepScanMemory>(m => capturedMemory = m)
            .Returns(Task.CompletedTask);

        // Act
        await _service.RecordCleanupDecisionAsync(opportunity, userApproved: true);

        // Assert
        Assert.NotNull(capturedMemory);
        Assert.Equal(DeepScanMemoryType.CleanupDecision, capturedMemory.Type);
        Assert.Equal("approved", capturedMemory.Decision);
    }

    [Fact]
    public async Task RecordCleanupDecisionAsync_IncludesRiskInMetadata()
    {
        // Arrange
        var opportunity = new CleanupOpportunity
        {
            Type = CleanupType.RecycleBin,
            Path = "C:\\$Recycle.Bin",
            Risk = CleanupRisk.Medium,
            FileCount = 45
        };

        DeepScanMemory? capturedMemory = null;
        _ragStoreMock.Setup(r => r.StoreMemoryAsync(It.IsAny<DeepScanMemory>()))
            .Callback<DeepScanMemory>(m => capturedMemory = m)
            .Returns(Task.CompletedTask);

        // Act
        await _service.RecordCleanupDecisionAsync(opportunity, true);

        // Assert
        Assert.NotNull(capturedMemory?.Metadata);
        Assert.Equal("RecycleBin", capturedMemory.Metadata["cleanupType"]);
        Assert.Equal("Medium", capturedMemory.Metadata["risk"]);
        Assert.Equal("45", capturedMemory.Metadata["fileCount"]);
    }

    #endregion

    #region RecordUserPreferenceAsync Tests

    [Fact]
    public async Task RecordUserPreferenceAsync_StoresPreferenceCorrectly()
    {
        // Arrange
        DeepScanMemory? capturedMemory = null;
        _ragStoreMock.Setup(r => r.StoreMemoryAsync(It.IsAny<DeepScanMemory>()))
            .Callback<DeepScanMemory>(m => capturedMemory = m)
            .Returns(Task.CompletedTask);

        // Act
        await _service.RecordUserPreferenceAsync(
            "relocation",
            "preferredVideoDrive",
            "E:\\",
            "Faster SSD for videos");

        // Assert
        Assert.NotNull(capturedMemory);
        Assert.Equal(DeepScanMemoryType.UserPreference, capturedMemory.Type);
        Assert.Equal("E:\\", capturedMemory.Decision);
        Assert.True(capturedMemory.UserAgreed);
        Assert.Equal(1.0, capturedMemory.AiConfidence); // Preferences are definitive
    }

    #endregion

    #region LearnFilePatternAsync Tests

    [Fact]
    public async Task LearnFilePatternAsync_StoresPatternCorrectly()
    {
        // Arrange
        DeepScanMemory? capturedMemory = null;
        _ragStoreMock.Setup(r => r.StoreMemoryAsync(It.IsAny<DeepScanMemory>()))
            .Callback<DeepScanMemory>(m => capturedMemory = m)
            .Returns(Task.CompletedTask);

        // Act
        await _service.LearnFilePatternAsync(
            ".log",
            "logs",
            "cleanup",
            userApproved: true,
            "Application log files");

        // Assert
        Assert.NotNull(capturedMemory);
        Assert.Equal(DeepScanMemoryType.FilePatternLearning, capturedMemory.Type);
        Assert.Equal("cleanup", capturedMemory.Decision);
        Assert.Equal(".log", capturedMemory.Metadata?["file_types"]);
    }

    [Fact]
    public async Task LearnFilePatternAsync_SetsLowerConfidenceForRejectedPatterns()
    {
        // Arrange
        DeepScanMemory? capturedMemory = null;
        _ragStoreMock.Setup(r => r.StoreMemoryAsync(It.IsAny<DeepScanMemory>()))
            .Callback<DeepScanMemory>(m => capturedMemory = m)
            .Returns(Task.CompletedTask);

        // Act
        await _service.LearnFilePatternAsync(".db", "database", "keep", userApproved: false);

        // Assert
        Assert.NotNull(capturedMemory);
        Assert.Equal(0.3, capturedMemory.AiConfidence);
        Assert.False(capturedMemory.UserAgreed);
    }

    #endregion

    #region LearnAppCategoryAsync Tests

    [Fact]
    public async Task LearnAppCategoryAsync_StoresEssentialApp()
    {
        // Arrange
        DeepScanMemory? capturedMemory = null;
        _ragStoreMock.Setup(r => r.StoreMemoryAsync(It.IsAny<DeepScanMemory>()))
            .Callback<DeepScanMemory>(m => capturedMemory = m)
            .Returns(Task.CompletedTask);

        // Act
        await _service.LearnAppCategoryAsync("Visual Studio", "Microsoft", "development", isEssential: true);

        // Assert
        Assert.NotNull(capturedMemory);
        Assert.Equal(DeepScanMemoryType.AppCategoryLearning, capturedMemory.Type);
        Assert.Equal("essential", capturedMemory.Decision);
        Assert.Equal("Visual Studio", capturedMemory.Metadata?["appName"]);
        Assert.Equal("True", capturedMemory.Metadata?["isEssential"]);
    }

    [Fact]
    public async Task LearnAppCategoryAsync_StoresNonEssentialApp()
    {
        // Arrange
        DeepScanMemory? capturedMemory = null;
        _ragStoreMock.Setup(r => r.StoreMemoryAsync(It.IsAny<DeepScanMemory>()))
            .Callback<DeepScanMemory>(m => capturedMemory = m)
            .Returns(Task.CompletedTask);

        // Act
        await _service.LearnAppCategoryAsync("Candy Crush", "King", "games", isEssential: false);

        // Assert
        Assert.NotNull(capturedMemory);
        Assert.Equal("non-essential", capturedMemory.Decision);
        Assert.Equal("False", capturedMemory.Metadata?["isEssential"]);
    }

    #endregion
}
