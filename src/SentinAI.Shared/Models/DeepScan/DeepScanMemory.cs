namespace SentinAI.Shared.Models.DeepScan;

/// <summary>
/// Memory entry for the RAG store - stores user decisions and patterns.
/// </summary>
public class DeepScanMemory
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public DeepScanMemoryType Type { get; set; }

    /// <summary>
    /// Context string used for similarity search.
    /// </summary>
    public string Context { get; set; } = "";

    /// <summary>
    /// The decision that was made.
    /// </summary>
    public string Decision { get; set; } = "";

    /// <summary>
    /// Whether the user agreed with AI recommendation.
    /// </summary>
    public bool UserAgreed { get; set; }

    /// <summary>
    /// Original AI confidence level.
    /// </summary>
    public double AiConfidence { get; set; }

    /// <summary>
    /// Original AI reasoning.
    /// </summary>
    public string? AiReasoning { get; set; }

    /// <summary>
    /// Additional metadata for the memory.
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>
    /// Embedding vector for similarity search.
    /// </summary>
    public float[]? Embedding { get; set; }
}

/// <summary>
/// Types of memories stored in the RAG system.
/// </summary>
public enum DeepScanMemoryType
{
    AppRemovalDecision,
    RelocationDecision,
    CleanupDecision,
    UserPreference,
    FilePatternLearning,
    AppCategoryLearning,
    CorrectionPattern
}

/// <summary>
/// Pattern learned for app removal decisions by publisher.
/// </summary>
public class AppRemovalPattern
{
    public string Publisher { get; set; } = "";
    public int TotalDecisions { get; set; }
    public int RemovalDecisions { get; set; }
    public double RemovalRate => TotalDecisions > 0 ? (double)RemovalDecisions / TotalDecisions : 0;
    public List<string> CommonReasons { get; set; } = new();
}

/// <summary>
/// Pattern learned for file relocation decisions by file type.
/// </summary>
public class FileRelocationPattern
{
    public string FileType { get; set; } = "";
    public int TotalDecisions { get; set; }
    public int RelocationDecisions { get; set; }
    public double RelocationRate => TotalDecisions > 0 ? (double)RelocationDecisions / TotalDecisions : 0;
    public string? PreferredTargetDrive { get; set; }
}

/// <summary>
/// Interface for the deep scan RAG store.
/// </summary>
public interface IDeepScanRagStore
{
    /// <summary>
    /// Stores a memory entry.
    /// </summary>
    Task StoreMemoryAsync(DeepScanMemory memory);

    /// <summary>
    /// Finds similar app decisions from past memories.
    /// </summary>
    Task<List<DeepScanMemory>> FindSimilarAppDecisionsAsync(InstalledApp app);

    /// <summary>
    /// Finds similar file decisions from past memories.
    /// </summary>
    Task<List<DeepScanMemory>> FindSimilarFileDecisionsAsync(FileCluster cluster);

    /// <summary>
    /// Gets app removal patterns for a publisher.
    /// </summary>
    Task<AppRemovalPattern> GetAppRemovalPatternsAsync(string publisher);

    /// <summary>
    /// Gets file relocation patterns for a file type.
    /// </summary>
    Task<FileRelocationPattern> GetRelocationPatternsAsync(string fileType);

    /// <summary>
    /// Gets overall learning statistics.
    /// </summary>
    Task<DeepScanLearningStats> GetLearningStatsAsync();
}
