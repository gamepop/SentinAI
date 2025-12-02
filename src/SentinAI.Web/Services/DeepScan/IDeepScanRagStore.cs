using SentinAI.Shared.Models.DeepScan;

namespace SentinAI.Web.Services.DeepScan;

/// <summary>
/// Interface for storing and retrieving deep scan memories for AI learning.
/// </summary>
public interface IDeepScanRagStore
{
    /// <summary>
    /// Stores a memory for future learning.
    /// </summary>
    Task StoreMemoryAsync(DeepScanMemory memory);
    
    /// <summary>
    /// Stores an app removal decision.
    /// </summary>
    Task StoreAppDecisionAsync(InstalledApp app, bool removed, string? userReason);
    
    /// <summary>
    /// Stores a file relocation decision.
    /// </summary>
    Task StoreRelocationDecisionAsync(FileCluster files, bool relocated, string? targetDrive);
    
    /// <summary>
    /// Stores a cleanup decision.
    /// </summary>
    Task StoreCleanupDecisionAsync(CleanupOpportunity item, bool cleaned);
    
    /// <summary>
    /// Finds similar past app decisions.
    /// </summary>
    Task<List<DeepScanMemory>> FindSimilarAppDecisionsAsync(InstalledApp app, int limit = 5);
    
    /// <summary>
    /// Finds similar past file decisions.
    /// </summary>
    Task<List<DeepScanMemory>> FindSimilarFileDecisionsAsync(FileCluster files, int limit = 5);
    
    /// <summary>
    /// Finds user preferences for a category.
    /// </summary>
    Task<List<DeepScanMemory>> FindUserPreferencesAsync(string category);
    
    /// <summary>
    /// Gets app removal patterns for a publisher.
    /// </summary>
    Task<AppRemovalPattern> GetAppRemovalPatternsAsync(string publisher);
    
    /// <summary>
    /// Gets file relocation patterns for a file type.
    /// </summary>
    Task<FileRelocationPattern> GetRelocationPatternsAsync(string fileType);
    
    /// <summary>
    /// Gets learning statistics.
    /// </summary>
    Task<DeepScanLearningStats> GetLearningStatsAsync();
    
    /// <summary>
    /// Searches memories by semantic similarity.
    /// </summary>
    Task<List<SimilarMemoryResult>> SearchMemoriesAsync(string query, int limit = 10);
}

/// <summary>
/// In-memory implementation of the deep scan RAG store for development/testing.
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
    
    public Task StoreAppDecisionAsync(InstalledApp app, bool removed, string? userReason)
    {
        var memory = new DeepScanMemory
        {
            Type = DeepScanMemoryType.AppRemovalDecision,
            Context = $"App: {app.Name} by {app.Publisher}, Size: {app.TotalSizeFormatted}, Category: {app.Category}",
            Decision = removed 
                ? $"Removed. {userReason ?? "User approved removal"}"
                : $"Kept. {userReason ?? "User rejected removal"}",
            UserAgreed = true,
            Metadata = new Dictionary<string, string>
            {
                ["publisher"] = app.Publisher,
                ["category"] = app.Category.ToString(),
                ["size_bytes"] = app.TotalSizeBytes.ToString(),
                ["app_name"] = app.Name
            }
        };
        
        return StoreMemoryAsync(memory);
    }
    
    public Task StoreRelocationDecisionAsync(FileCluster files, bool relocated, string? targetDrive)
    {
        var memory = new DeepScanMemory
        {
            Type = DeepScanMemoryType.RelocationDecision,
            Context = $"Files: {files.BasePath}, Count: {files.FileCount}, Size: {files.TotalSizeFormatted}, Types: {string.Join(",", files.FileTypes)}",
            Decision = relocated
                ? $"Relocated to {targetDrive}"
                : "Kept in original location",
            UserAgreed = true,
            Metadata = new Dictionary<string, string>
            {
                ["file_types"] = string.Join(",", files.FileTypes),
                ["cluster_type"] = files.Type.ToString(),
                ["size_bytes"] = files.TotalBytes.ToString(),
                ["target_drive"] = targetDrive ?? ""
            }
        };
        
        return StoreMemoryAsync(memory);
    }
    
    public Task StoreCleanupDecisionAsync(CleanupOpportunity item, bool cleaned)
    {
        var memory = new DeepScanMemory
        {
            Type = DeepScanMemoryType.CleanupDecision,
            Context = $"Cleanup: {item.Description}, Path: {item.Path}, Size: {item.BytesFormatted}",
            Decision = cleaned ? "Cleaned" : "Skipped",
            UserAgreed = true,
            Metadata = new Dictionary<string, string>
            {
                ["cleanup_type"] = item.Type.ToString(),
                ["associated_app"] = item.AssociatedApp ?? "",
                ["size_bytes"] = item.Bytes.ToString()
            }
        };
        
        return StoreMemoryAsync(memory);
    }
    
    public Task<List<DeepScanMemory>> FindSimilarAppDecisionsAsync(InstalledApp app, int limit = 5)
    {
        lock (_lock)
        {
            var results = _memories
                .Where(m => m.Type == DeepScanMemoryType.AppRemovalDecision)
                .Where(m => 
                    m.Publisher?.Equals(app.Publisher, StringComparison.OrdinalIgnoreCase) == true ||
                    m.Category?.Equals(app.Category.ToString(), StringComparison.OrdinalIgnoreCase) == true)
                .OrderByDescending(m => m.Timestamp)
                .Take(limit)
                .ToList();
            
            return Task.FromResult(results);
        }
    }
    
    public Task<List<DeepScanMemory>> FindSimilarFileDecisionsAsync(FileCluster files, int limit = 5)
    {
        lock (_lock)
        {
            var fileTypes = files.FileTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            
            var results = _memories
                .Where(m => m.Type == DeepScanMemoryType.RelocationDecision)
                .Where(m => 
                {
                    var storedTypes = m.FileTypes?.Split(',') ?? Array.Empty<string>();
                    return storedTypes.Any(t => fileTypes.Contains(t));
                })
                .OrderByDescending(m => m.Timestamp)
                .Take(limit)
                .ToList();
            
            return Task.FromResult(results);
        }
    }
    
    public Task<List<DeepScanMemory>> FindUserPreferencesAsync(string category)
    {
        lock (_lock)
        {
            var results = _memories
                .Where(m => m.Type == DeepScanMemoryType.UserPreference)
                .Where(m => m.Context.Contains(category, StringComparison.OrdinalIgnoreCase))
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
            var decisions = _memories
                .Where(m => m.Type == DeepScanMemoryType.AppRemovalDecision)
                .Where(m => m.Publisher?.Equals(publisher, StringComparison.OrdinalIgnoreCase) == true)
                .ToList();
            
            var pattern = new AppRemovalPattern
            {
                Publisher = publisher,
                TotalDecisions = decisions.Count,
                RemovalCount = decisions.Count(d => d.Decision.StartsWith("Removed")),
                KeepCount = decisions.Count(d => d.Decision.StartsWith("Kept")),
                AppsRemoved = decisions
                    .Where(d => d.Decision.StartsWith("Removed"))
                    .Select(d => d.Metadata.GetValueOrDefault("app_name") ?? "")
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct()
                    .ToList(),
                AppsKept = decisions
                    .Where(d => d.Decision.StartsWith("Kept"))
                    .Select(d => d.Metadata.GetValueOrDefault("app_name") ?? "")
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct()
                    .ToList()
            };
            
            return Task.FromResult(pattern);
        }
    }
    
    public Task<FileRelocationPattern> GetRelocationPatternsAsync(string fileType)
    {
        lock (_lock)
        {
            var decisions = _memories
                .Where(m => m.Type == DeepScanMemoryType.RelocationDecision)
                .Where(m => m.FileTypes?.Contains(fileType, StringComparison.OrdinalIgnoreCase) == true)
                .ToList();
            
            var pattern = new FileRelocationPattern
            {
                FileType = fileType,
                TotalDecisions = decisions.Count,
                RelocatedCount = decisions.Count(d => d.Decision.StartsWith("Relocated")),
                KeptCount = decisions.Count(d => d.Decision.StartsWith("Kept")),
                PreferredTargetDrive = decisions
                    .Select(d => d.Metadata.GetValueOrDefault("target_drive"))
                    .Where(d => !string.IsNullOrEmpty(d))
                    .GroupBy(d => d)
                    .OrderByDescending(g => g.Count())
                    .FirstOrDefault()?.Key
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
                UserCorrections = _memories.Count(m => m.Type == DeepScanMemoryType.CorrectionPattern),
                FirstMemory = _memories.MinBy(m => m.Timestamp)?.Timestamp,
                LastMemory = _memories.MaxBy(m => m.Timestamp)?.Timestamp,
                AiAccuracyRate = _memories.Any() 
                    ? _memories.Count(m => m.UserAgreed) / (double)_memories.Count 
                    : 1.0
            };
            
            return Task.FromResult(stats);
        }
    }
    
    public Task<List<SimilarMemoryResult>> SearchMemoriesAsync(string query, int limit = 10)
    {
        lock (_lock)
        {
            // Simple keyword search (in production, use vector similarity)
            var queryTerms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            
            var results = _memories
                .Select(m => new SimilarMemoryResult
                {
                    Memory = m,
                    SimilarityScore = CalculateSimpleSimilarity(m, queryTerms)
                })
                .Where(r => r.SimilarityScore > 0)
                .OrderByDescending(r => r.SimilarityScore)
                .Take(limit)
                .ToList();
            
            return Task.FromResult(results);
        }
    }
    
    private double CalculateSimpleSimilarity(DeepScanMemory memory, string[] queryTerms)
    {
        var text = $"{memory.Context} {memory.Decision}".ToLowerInvariant();
        var matches = queryTerms.Count(t => text.Contains(t));
        return matches / (double)queryTerms.Length;
    }
}
