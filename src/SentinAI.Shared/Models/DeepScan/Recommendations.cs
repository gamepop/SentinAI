namespace SentinAI.Shared.Models.DeepScan;

/// <summary>
/// Recommendation for app removal.
/// </summary>
public class AppRemovalRecommendation
{
    public InstalledApp? App { get; set; }
    public bool ShouldRemove { get; set; }
    public double Confidence { get; set; }
    public AppRemovalCategory Category { get; set; }
    public string? AiReason { get; set; }

    // Actions available
    public bool CanUninstall { get; set; }
    public bool CanClearData { get; set; }
    public bool CanClearCache { get; set; }

    // Potential savings
    public long UninstallSavings { get; set; }
    public long DataClearSavings { get; set; }
    public long CacheClearSavings { get; set; }
    public long TotalPotentialSavings => UninstallSavings + DataClearSavings + CacheClearSavings;

    // Learning context
    public int SimilarPastDecisions { get; set; }
    public string? LearnedInfluence { get; set; }
    public bool LearnedFromPast => SimilarPastDecisions > 0;

    // Status tracking
    public RecommendationStatus Status { get; set; } = RecommendationStatus.Pending;

    // Formatted properties
    public string TotalSavingsFormatted => FormatBytes(TotalPotentialSavings);
    public string ConfidenceFormatted => $"{Confidence:P0}";

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
/// Category for app removal recommendation.
/// </summary>
public enum AppRemovalCategory
{
    KeepRecommended,
    Optional,
    Unused,
    LargeUnused,
    Bloatware,
    Redundant,
    OutdatedVersion
}

/// <summary>
/// Recommendation for file relocation.
/// </summary>
public class RelocationRecommendation
{
    public FileCluster? Cluster { get; set; }
    public bool ShouldRelocate { get; set; }
    public int Priority { get; set; } = 1; // 1-5, higher = more important
    public string? TargetDrive { get; set; }
    public double Confidence { get; set; }
    public bool RequiresJunction { get; set; }
    public string? AiReason { get; set; }

    // Learning context
    public int SimilarPastDecisions { get; set; }
    public string? LearnedInfluence { get; set; }

    // Status tracking
    public RecommendationStatus Status { get; set; } = RecommendationStatus.Pending;

    public string PriorityLabel => Priority switch
    {
        5 => "Critical",
        4 => "High",
        3 => "Medium",
        2 => "Low",
        _ => "Optional"
    };
}

/// <summary>
/// Opportunity for cleanup.
/// </summary>
public class CleanupOpportunity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public CleanupType Type { get; set; }
    public string Path { get; set; } = "";
    public string Description { get; set; } = "";
    public long Bytes { get; set; }
    public int FileCount { get; set; }
    public CleanupRisk Risk { get; set; }
    public string? AssociatedApp { get; set; }
    public double Confidence { get; set; } = 0.8;
    public string? AiReason { get; set; }

    // Status tracking
    public RecommendationStatus Status { get; set; } = RecommendationStatus.Pending;

    // Formatted properties
    public string BytesFormatted => FormatBytes(Bytes);
    public string RiskLabel => Risk.ToString();
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

/// <summary>
/// Type of cleanup opportunity.
/// </summary>
public enum CleanupType
{
    WindowsTemp,
    UserTemp,
    BrowserCache,
    ThumbnailCache,
    WindowsUpdateCache,
    RecycleBin,
    AppCache,
    LogFiles,
    OldDownloads,
    DuplicateFiles,
    EmptyFolders,
    Other
}

/// <summary>
/// Risk level for cleanup operations.
/// </summary>
public enum CleanupRisk
{
    None,
    Low,
    Medium,
    High
}

/// <summary>
/// Status of a recommendation.
/// </summary>
public enum RecommendationStatus
{
    Pending,
    Approved,
    Rejected,
    Executed,
    Failed
}

/// <summary>
/// Group of duplicate files.
/// </summary>
public class DuplicateGroup
{
    public string Hash { get; set; } = "";
    public long FileSize { get; set; }
    public List<DuplicateFileEntry> Files { get; set; } = new();

    public int DuplicateCount => Files.Count - 1;
    public long WastedBytes => FileSize * DuplicateCount;
    public string WastedBytesFormatted => FormatBytes(WastedBytes);
    public string FileSizeFormatted => FormatBytes(FileSize);

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
/// Entry in a duplicate file group.
/// </summary>
public class DuplicateFileEntry
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime LastAccessed { get; set; }
    public DateTime LastModified { get; set; }
    public string DriveLetter { get; set; } = "";
    public int LocationPriority { get; set; } // Lower = more important location
    public bool IsSelected { get; set; }
}
