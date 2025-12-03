using SentinAI.Shared.Models.DeepScan;
using SentinAI.Web.Services.DeepScan;

namespace SentinAI.Web.Tests.Services.DeepScan;

public class InMemoryDeepScanRagStoreTests
{
    private readonly InMemoryDeepScanRagStore _store;

    public InMemoryDeepScanRagStoreTests()
    {
        _store = new InMemoryDeepScanRagStore();
    }

    [Fact]
    public async Task StoreMemoryAsync_StoresMemory()
    {
        // Arrange
        var memory = new DeepScanMemory
        {
            Type = DeepScanMemoryType.AppRemovalDecision,
            Decision = "approved",
            UserAgreed = true,
            Metadata = new Dictionary<string, string>
            {
                { "publisher", "TestPublisher" },
                { "category", "Utility" }
            }
        };

        // Act
        await _store.StoreMemoryAsync(memory);
        var stats = await _store.GetLearningStatsAsync();

        // Assert
        Assert.Equal(1, stats.TotalMemories);
        Assert.Equal(1, stats.AppDecisions);
    }

    [Fact]
    public async Task FindSimilarAppDecisionsAsync_FindsByPublisher()
    {
        // Arrange
        var memory = new DeepScanMemory
        {
            Type = DeepScanMemoryType.AppRemovalDecision,
            Decision = "approved",
            UserAgreed = true,
            Metadata = new Dictionary<string, string>
            {
                { "publisher", "Microsoft" },
                { "category", "System" }
            }
        };
        await _store.StoreMemoryAsync(memory);

        var app = new InstalledApp
        {
            Name = "Test App",
            Publisher = "Microsoft",
            Category = AppCategory.Utility
        };

        // Act
        var results = await _store.FindSimilarAppDecisionsAsync(app);

        // Assert
        Assert.Single(results);
        Assert.Equal("approved", results[0].Decision);
    }

    [Fact]
    public async Task FindSimilarAppDecisionsAsync_FindsByCategory()
    {
        // Arrange
        var memory = new DeepScanMemory
        {
            Type = DeepScanMemoryType.AppRemovalDecision,
            Decision = "rejected",
            UserAgreed = false,
            Metadata = new Dictionary<string, string>
            {
                { "publisher", "OtherPublisher" },
                { "category", "Gaming" }
            }
        };
        await _store.StoreMemoryAsync(memory);

        var app = new InstalledApp
        {
            Name = "Test Game",
            Publisher = "DifferentPublisher",
            Category = AppCategory.Gaming
        };

        // Act
        var results = await _store.FindSimilarAppDecisionsAsync(app);

        // Assert
        Assert.Single(results);
    }

    [Fact]
    public async Task FindSimilarFileDecisionsAsync_FindsByClusterType()
    {
        // Arrange
        var memory = new DeepScanMemory
        {
            Type = DeepScanMemoryType.RelocationDecision,
            Decision = "approved",
            UserAgreed = true,
            Metadata = new Dictionary<string, string>
            {
                { "clusterType", "MediaVideos" },
                { "actualTargetDrive", "D:\\" }
            }
        };
        await _store.StoreMemoryAsync(memory);

        var cluster = new FileCluster
        {
            Name = "Videos",
            Type = FileClusterType.MediaVideos,
            BasePath = "C:\\Users\\Test\\Videos"
        };

        // Act
        var results = await _store.FindSimilarFileDecisionsAsync(cluster);

        // Assert
        Assert.Single(results);
    }

    [Fact]
    public async Task GetAppRemovalPatternsAsync_ReturnsCorrectCounts()
    {
        // Arrange
        for (int i = 0; i < 3; i++)
        {
            await _store.StoreMemoryAsync(new DeepScanMemory
            {
                Type = DeepScanMemoryType.AppRemovalDecision,
                Decision = "approved",
                UserAgreed = true,
                Metadata = new Dictionary<string, string> { { "publisher", "Adobe" } }
            });
        }
        await _store.StoreMemoryAsync(new DeepScanMemory
        {
            Type = DeepScanMemoryType.AppRemovalDecision,
            Decision = "rejected",
            UserAgreed = true,
            Metadata = new Dictionary<string, string> { { "publisher", "Adobe" } }
        });

        // Act
        var pattern = await _store.GetAppRemovalPatternsAsync("Adobe");

        // Assert
        Assert.Equal("Adobe", pattern.Publisher);
        Assert.Equal(4, pattern.TotalDecisions);
        Assert.Equal(3, pattern.RemovalDecisions);
    }

    [Fact]
    public async Task GetRelocationPatternsAsync_ReturnsPreferredDrive()
    {
        // Arrange
        // 3 relocations to D:\, 1 to E:\
        for (int i = 0; i < 3; i++)
        {
            await _store.StoreMemoryAsync(new DeepScanMemory
            {
                Type = DeepScanMemoryType.RelocationDecision,
                Decision = "approved",
                UserAgreed = true,
                Metadata = new Dictionary<string, string>
                {
                    { "clusterType", "MediaVideos" },
                    { "actualTargetDrive", "D:\\" }
                }
            });
        }
        await _store.StoreMemoryAsync(new DeepScanMemory
        {
            Type = DeepScanMemoryType.RelocationDecision,
            Decision = "approved",
            UserAgreed = true,
            Metadata = new Dictionary<string, string>
            {
                { "clusterType", "MediaVideos" },
                { "actualTargetDrive", "E:\\" }
            }
        });

        // Act
        var pattern = await _store.GetRelocationPatternsAsync("MediaVideos");

        // Assert
        Assert.Equal("MediaVideos", pattern.FileType);
        Assert.Equal(4, pattern.TotalDecisions);
        Assert.Equal(4, pattern.RelocationDecisions);
        Assert.Equal("D:\\", pattern.PreferredTargetDrive);
    }

    [Fact]
    public async Task GetLearningStatsAsync_ReturnsCorrectStats()
    {
        // Arrange
        await _store.StoreMemoryAsync(new DeepScanMemory
        {
            Type = DeepScanMemoryType.AppRemovalDecision,
            Decision = "approved",
            UserAgreed = true
        });
        await _store.StoreMemoryAsync(new DeepScanMemory
        {
            Type = DeepScanMemoryType.RelocationDecision,
            Decision = "approved",
            UserAgreed = false
        });
        await _store.StoreMemoryAsync(new DeepScanMemory
        {
            Type = DeepScanMemoryType.CleanupDecision,
            Decision = "approved",
            UserAgreed = true
        });

        // Act
        var stats = await _store.GetLearningStatsAsync();

        // Assert
        Assert.Equal(3, stats.TotalMemories);
        Assert.Equal(1, stats.AppDecisions);
        Assert.Equal(1, stats.RelocationDecisions);
        Assert.Equal(1, stats.CleanupDecisions);
        // 2 agreed out of 3 = 66.67%
        Assert.True(stats.AiAccuracyRate > 0.6 && stats.AiAccuracyRate < 0.7);
    }

    [Fact]
    public async Task GetLearningStatsAsync_NoMemories_ReturnsDefaultAccuracy()
    {
        // Act
        var stats = await _store.GetLearningStatsAsync();

        // Assert
        Assert.Equal(0, stats.TotalMemories);
        Assert.Equal(0.75, stats.AiAccuracyRate); // Default baseline
    }

    [Fact]
    public async Task FindSimilarAppDecisionsAsync_LimitsTo10Results()
    {
        // Arrange
        for (int i = 0; i < 15; i++)
        {
            await _store.StoreMemoryAsync(new DeepScanMemory
            {
                Type = DeepScanMemoryType.AppRemovalDecision,
                Decision = "approved",
                UserAgreed = true,
                Metadata = new Dictionary<string, string> { { "publisher", "TestPub" } }
            });
        }

        var app = new InstalledApp { Publisher = "TestPub" };

        // Act
        var results = await _store.FindSimilarAppDecisionsAsync(app);

        // Assert
        Assert.Equal(10, results.Count);
    }
}
