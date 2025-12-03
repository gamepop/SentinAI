namespace SentinAI.Shared.Models.DeepScan;

/// <summary>
/// Represents a deep scan session with all its state and results.
/// </summary>
public class DeepScanSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DeepScanState State { get; set; } = DeepScanState.Idle;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public DeepScanProgress Progress { get; set; } = new();
    public DeepScanSummary? Summary { get; set; }

    // Results
    public List<AppRemovalRecommendation>? AppRemovalRecommendations { get; set; }
    public List<RelocationRecommendation>? RelocationRecommendations { get; set; }
    public List<CleanupOpportunity>? CleanupOpportunities { get; set; }
    public List<DuplicateGroup>? DuplicateGroups { get; set; }

    public string? ErrorMessage { get; set; }
}

/// <summary>
/// State of the deep scan process.
/// </summary>
public enum DeepScanState
{
    Idle,
    Initializing,
    ScanningFiles,
    ScanningApps,
    AnalyzingWithAi,
    FindingDuplicates,
    GeneratingRecommendations,
    AwaitingApproval,
    Executing,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Progress information for ongoing scan.
/// </summary>
public class DeepScanProgress
{
    public string CurrentPhase { get; set; } = "Initializing";
    public double OverallProgress { get; set; }
    public long FilesScanned { get; set; }
    public long BytesAnalyzed { get; set; }
    public int AppsDiscovered { get; set; }
    public int RecommendationsGenerated { get; set; }
    public string? CurrentPath { get; set; }
}

/// <summary>
/// Summary of scan results.
/// </summary>
public class DeepScanSummary
{
    public long PotentialSpaceSavings { get; set; }
    public int TotalRecommendations { get; set; }
    public int AppsRecommendedForRemoval { get; set; }
    public int FileClustersToRelocate { get; set; }
    public int CleanupOpportunitiesFound { get; set; }
    public int DuplicateGroupsFound { get; set; }
    public long DuplicateSpaceSavings { get; set; }

    public string PotentialSpaceSavingsFormatted => FormatBytes(PotentialSpaceSavings);

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }
}

/// <summary>
/// Statistics about learning progress.
/// </summary>
public class DeepScanLearningStats
{
    public int TotalMemories { get; set; }
    public int AppDecisions { get; set; }
    public int RelocationDecisions { get; set; }
    public int CleanupDecisions { get; set; }
    public double AiAccuracyRate { get; set; }
    public DateTime? LastLearningDate { get; set; }
}
