using SentinAI.Shared.Models.DeepScan;

namespace SentinAI.Web.Services.DeepScan;

/// <summary>
/// In-memory implementation of the RAG store for deep scan learning.
/// </summary>
public class InMemoryDeepScanRagStore : IDeepScanRagStore
{
    private readonly List<DeepScanMemory> _memories = new();
    private readonly object _lock = new();

    public Task StoreMemoryAsync(DeepScanMemory memory)
    {
        lock (_lock)
        {
            _memories.Add(memory);
        }
        return Task.CompletedTask;
    }

    public Task<List<DeepScanMemory>> FindSimilarAppDecisionsAsync(InstalledApp app)
    {
        lock (_lock)
        {
            var results = _memories
                .Where(m => m.Type == DeepScanMemoryType.AppRemovalDecision)
                .Where(m =>
                    m.Metadata.TryGetValue("publisher", out var pub) &&
                    pub.Equals(app.Publisher, StringComparison.OrdinalIgnoreCase) ||
                    m.Metadata.TryGetValue("category", out var cat) &&
                    cat.Equals(app.Category.ToString(), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.Timestamp)
                .Take(10)
                .ToList();

            return Task.FromResult(results);
        }
    }

    public Task<List<DeepScanMemory>> FindSimilarFileDecisionsAsync(FileCluster cluster)
    {
        lock (_lock)
        {
            var results = _memories
                .Where(m => m.Type == DeepScanMemoryType.RelocationDecision)
                .Where(m =>
                    m.Metadata.TryGetValue("clusterType", out var type) &&
                    type.Equals(cluster.Type.ToString(), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.Timestamp)
                .Take(10)
                .ToList();

            return Task.FromResult(results);
        }
    }

    public Task<AppRemovalPattern> GetAppRemovalPatternsAsync(string publisher)
    {
        lock (_lock)
        {
            var publisherDecisions = _memories
                .Where(m => m.Type == DeepScanMemoryType.AppRemovalDecision)
                .Where(m => m.Metadata.TryGetValue("publisher", out var pub) &&
                            pub.Equals(publisher, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var pattern = new AppRemovalPattern
            {
                Publisher = publisher,
                TotalDecisions = publisherDecisions.Count,
                RemovalDecisions = publisherDecisions.Count(m => m.Decision == "approved")
            };

            return Task.FromResult(pattern);
        }
    }

    public Task<FileRelocationPattern> GetRelocationPatternsAsync(string fileType)
    {
        lock (_lock)
        {
            var fileTypeDecisions = _memories
                .Where(m => m.Type == DeepScanMemoryType.RelocationDecision)
                .Where(m => m.Metadata.TryGetValue("clusterType", out var type) &&
                            type.Equals(fileType, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var preferredDrive = fileTypeDecisions
                .Where(m => m.Decision == "approved")
                .SelectMany(m => m.Metadata.TryGetValue("actualTargetDrive", out var drive) ? new[] { drive } : Array.Empty<string>())
                .GroupBy(d => d)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault()?.Key;

            var pattern = new FileRelocationPattern
            {
                FileType = fileType,
                TotalDecisions = fileTypeDecisions.Count,
                RelocationDecisions = fileTypeDecisions.Count(m => m.Decision == "approved"),
                PreferredTargetDrive = preferredDrive
            };

            return Task.FromResult(pattern);
        }
    }

    public Task<DeepScanLearningStats> GetLearningStatsAsync()
    {
        lock (_lock)
        {
            var stats = new DeepScanLearningStats
            {
                TotalMemories = _memories.Count,
                AppDecisions = _memories.Count(m => m.Type == DeepScanMemoryType.AppRemovalDecision),
                RelocationDecisions = _memories.Count(m => m.Type == DeepScanMemoryType.RelocationDecision),
                CleanupDecisions = _memories.Count(m => m.Type == DeepScanMemoryType.CleanupDecision),
                AiAccuracyRate = CalculateAccuracyRate(),
                LastLearningDate = _memories.MaxBy(m => m.Timestamp)?.Timestamp
            };

            return Task.FromResult(stats);
        }
    }

    private double CalculateAccuracyRate()
    {
        var decisions = _memories
            .Where(m => m.Type is DeepScanMemoryType.AppRemovalDecision
                        or DeepScanMemoryType.RelocationDecision
                        or DeepScanMemoryType.CleanupDecision)
            .ToList();

        if (!decisions.Any()) return 0.75; // Default baseline

        var agreements = decisions.Count(m => m.UserAgreed);
        return (double)agreements / decisions.Count;
    }
}
