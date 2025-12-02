namespace SentinAI.Shared.Models.DeepScan;

/// <summary>
/// Represents a recommendation to relocate files to another drive.
/// </summary>
public class RelocationRecommendation
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public FileCluster Cluster { get; set; } = new();
    
    public bool ShouldRelocate { get; set; }
    public string? TargetDrive { get; set; }
    public string? TargetPath { get; set; }
    public int Priority { get; set; } // 1-5, 5 = highest priority
    public double Confidence { get; set; }
    
    public bool RequiresJunction { get; set; }
    public string? JunctionSource { get; set; }
    
    public string AiReason { get; set; } = "";
    public string? LearnedInfluence { get; set; }
    public int SimilarPastDecisions { get; set; }
    public bool LearnedFromPast => SimilarPastDecisions > 0;
    
    // Execution status
    public RecommendationStatus Status { get; set; } = RecommendationStatus.Pending;
    public string? Error { get; set; }
    
    // For UI
    public string PriorityLabel => Priority switch
    {
        5 => "Critical",
        4 => "High",
        3 => "Medium",
        2 => "Low",
        _ => "Optional"
    };
    
    public string ConfidenceFormatted => $"{Confidence:P0}";
}

/// <summary>
/// Represents a recommendation to remove an application.
/// </summary>
public class AppRemovalRecommendation
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public InstalledApp App { get; set; } = new();
    
    public bool ShouldRemove { get; set; }
    public AppRemovalCategory Category { get; set; }
    public double Confidence { get; set; }
    
    public string AiReason { get; set; } = "";
    public string? LearnedInfluence { get; set; }
    public int SimilarPastDecisions { get; set; }
    public bool LearnedFromPast => SimilarPastDecisions > 0;
    
    // What can be done
    public bool CanUninstall { get; set; }
    public bool CanClearData { get; set; }
    public bool CanClearCache { get; set; }
    
    // Space savings
    public long UninstallSavings { get; set; }
    public long DataClearSavings { get; set; }
    public long CacheClearSavings { get; set; }
    public long TotalPotentialSavings => UninstallSavings + DataClearSavings + CacheClearSavings;
    
    public string UninstallSavingsFormatted => FormatBytes(UninstallSavings);
    public string TotalSavingsFormatted => FormatBytes(TotalPotentialSavings);
    public string ConfidenceFormatted => $"{Confidence:P0}";
    
    // Execution status
    public RecommendationStatus Status { get; set; } = RecommendationStatus.Pending;
    public string? Error { get; set; }
    
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

public enum AppRemovalCategory
{
    Bloatware,      // Pre-installed unnecessary apps
    Unused,         // Not accessed in 90+ days
    Redundant,      // Multiple apps doing same thing
    LargeUnused,    // Large apps that are unused
    Optional,       // Could be removed but might be useful
    KeepRecommended // AI recommends keeping
}

/// <summary>
/// Represents a cleanup opportunity (cache, temp, etc.).
/// </summary>
public class CleanupOpportunity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public CleanupType Type { get; set; }
    public string Path { get; set; } = "";
    public string Description { get; set; } = "";
    
    public long Bytes { get; set; }
    public int FileCount { get; set; }
    public double Confidence { get; set; }
    public CleanupRisk Risk { get; set; }
    
    public string? AssociatedApp { get; set; }
    public string AiReason { get; set; } = "";
    public string? LearnedInfluence { get; set; }
    public int SimilarPastDecisions { get; set; }
    public bool LearnedFromPast => SimilarPastDecisions > 0;
    
    // Execution status
    public RecommendationStatus Status { get; set; } = RecommendationStatus.Pending;
    public string? Error { get; set; }
    
    public string BytesFormatted => FormatBytes(Bytes);
    public string ConfidenceFormatted => $"{Confidence:P0}";
    
    public string RiskLabel => Risk switch
    {
        CleanupRisk.None => "Safe",
        CleanupRisk.Low => "Low Risk",
        CleanupRisk.Medium => "Medium Risk",
        CleanupRisk.High => "High Risk",
        _ => "Unknown"
    };
    
    public string RiskColor => Risk switch
    {
        CleanupRisk.None => "#28a745",
        CleanupRisk.Low => "#17a2b8",
        CleanupRisk.Medium => "#ffc107",
        CleanupRisk.High => "#dc3545",
        _ => "#6c757d"
    };
    
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

public enum CleanupType
{
    WindowsTemp,
    UserTemp,
    BrowserCache,
    BrowserHistory,
    AppCache,
    AppLogs,
    WindowsUpdateCache,
    SystemRestorePoints,
    ThumbnailCache,
    FontCache,
    InstallerCache,
    CrashDumps,
    OldDownloads,
    RecycleBin,
    DeveloperCache,     // npm cache, nuget cache, etc.
    GameCache
}

public enum CleanupRisk
{
    None,       // Absolutely safe
    Low,        // Minor inconvenience if wrong
    Medium,     // May need to re-download/reconfigure
    High        // Could cause app issues
}

public enum RecommendationStatus
{
    Pending,
    Approved,
    Rejected,
    Executing,
    Completed,
    Failed,
    RolledBack
}
