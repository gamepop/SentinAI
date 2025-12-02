namespace SentinAI.Shared.Models.DeepScan;

/// <summary>
/// Memory types for deep scan RAG learning.
/// </summary>
public enum DeepScanMemoryType
{
    AppRemovalDecision,      // User approved/rejected app removal
    RelocationDecision,      // User approved/rejected file move
    CleanupDecision,         // User approved/rejected cleanup
    AppCategoryLearning,     // Learning app categories (bloatware, essential, etc.)
    FilePatternLearning,     // Learning file patterns (safe to move, must stay)
    UserPreference,          // User's general preferences
    CorrectionPattern        // When user corrected AI's decision
}

/// <summary>
/// Represents a stored memory from deep scan decisions for AI learning.
/// </summary>
public class DeepScanMemory
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DeepScanMemoryType Type { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Description of what was analyzed (app name, file path, etc.)
    /// </summary>
    public string Context { get; set; } = "";
    
    /// <summary>
    /// The user's decision and reasoning.
    /// </summary>
    public string Decision { get; set; } = "";
    
    /// <summary>
    /// The AI's original reasoning before user decision.
    /// </summary>
    public string AiReasoning { get; set; } = "";
    
    /// <summary>
    /// Whether the user agreed with the AI's recommendation.
    /// </summary>
    public bool UserAgreed { get; set; }
    
    /// <summary>
    /// AI's original confidence level.
    /// </summary>
    public double AiConfidence { get; set; }
    
    /// <summary>
    /// Additional metadata for filtering and learning.
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();
    
    // Common metadata accessors
    public string? Publisher => Metadata.GetValueOrDefault("publisher");
    public string? Category => Metadata.GetValueOrDefault("category");
    public string? FileTypes => Metadata.GetValueOrDefault("file_types");
    public long SizeBytes => long.TryParse(Metadata.GetValueOrDefault("size_bytes"), out var size) ? size : 0;
}

/// <summary>
/// Patterns learned from app removal decisions.
/// </summary>
public class AppRemovalPattern
{
    public string Publisher { get; set; } = "";
    public int TotalDecisions { get; set; }
    public int RemovalCount { get; set; }
    public int KeepCount { get; set; }
    public double RemovalRate => TotalDecisions > 0 ? RemovalCount / (double)TotalDecisions : 0;
    public string? CommonReason { get; set; }
    public List<string> AppsRemoved { get; set; } = new();
    public List<string> AppsKept { get; set; } = new();
}

/// <summary>
/// Patterns learned from file relocation decisions.
/// </summary>
public class FileRelocationPattern
{
    public string FileType { get; set; } = "";
    public int TotalDecisions { get; set; }
    public int RelocatedCount { get; set; }
    public int KeptCount { get; set; }
    public double RelocationRate => TotalDecisions > 0 ? RelocatedCount / (double)TotalDecisions : 0;
    public string? PreferredTargetDrive { get; set; }
    public string? CommonSourcePath { get; set; }
    public string? CommonReason { get; set; }
}

/// <summary>
/// Aggregated learning statistics for display.
/// </summary>
public class DeepScanLearningStats
{
    public int TotalMemories { get; set; }
    public int AppDecisions { get; set; }
    public int RelocationDecisions { get; set; }
    public int CleanupDecisions { get; set; }
    public int UserCorrections { get; set; }
    
    public double AiAccuracyRate { get; set; }
    public DateTime? FirstMemory { get; set; }
    public DateTime? LastMemory { get; set; }
    
    public List<AppRemovalPattern> TopPublisherPatterns { get; set; } = new();
    public List<FileRelocationPattern> TopFileTypePatterns { get; set; } = new();
    public List<string> LearnedPreferences { get; set; } = new();
}

/// <summary>
/// Search result for similar memories.
/// </summary>
public class SimilarMemoryResult
{
    public DeepScanMemory Memory { get; set; } = new();
    public double SimilarityScore { get; set; }
    public string? RelevanceReason { get; set; }
}
