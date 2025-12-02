namespace SentinAI.Shared.Models.DeepScan;

/// <summary>
/// Represents a deep scan session tracking progress, state, and results.
/// </summary>
public class DeepScanSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public DateTime? EndTime { get; set; }
    public DeepScanState State { get; set; } = DeepScanState.Initializing;
    public DeepScanOptions Options { get; set; } = new();
    
    // Progress tracking
    public DeepScanProgress Progress { get; set; } = new();
    
    // Results
    public List<DriveAnalysis> DriveAnalyses { get; set; } = new();
    public List<InstalledApp> DiscoveredApps { get; set; } = new();
    public List<FileCluster> FileClusters { get; set; } = new();
    public List<CleanupOpportunity> CleanupOpportunities { get; set; } = new();
    public List<RelocationRecommendation> RelocationRecommendations { get; set; } = new();
    public List<AppRemovalRecommendation> AppRemovalRecommendations { get; set; } = new();
    
    // Summary
    public DeepScanSummary? Summary { get; set; }
    
    public TimeSpan Duration => (EndTime ?? DateTime.UtcNow) - StartTime;
}

public enum DeepScanState
{
    Initializing,
    ScanningDrives,
    DiscoveringApps,
    AnalyzingFiles,
    ClusteringFiles,
    GeneratingRecommendations,
    AwaitingApproval,
    Executing,
    Completed,
    Cancelled,
    Failed
}

public class DeepScanOptions
{
    public List<string> TargetDrives { get; set; } = new() { "C:\\" };
    public bool IncludeSystemFiles { get; set; } = false;
    public bool IncludeHiddenFiles { get; set; } = true;
    public bool ScanInstalledApps { get; set; } = true;
    public bool ScanForDuplicates { get; set; } = true;
    public bool GenerateRelocationPlans { get; set; } = true;
    public long MinFileSizeBytes { get; set; } = 1024 * 1024; // 1MB minimum
    public int MaxFilesPerCluster { get; set; } = 1000;
    public List<string> ExcludePaths { get; set; } = new()
    {
        @"C:\Windows",
        @"C:\Program Files\WindowsApps",
        @"$Recycle.Bin"
    };
}

public class DeepScanProgress
{
    public string CurrentPhase { get; set; } = "Initializing";
    public string CurrentPath { get; set; } = "";
    public int TotalPhases { get; set; } = 6;
    public int CurrentPhaseNumber { get; set; } = 0;
    public double PhaseProgress { get; set; } = 0; // 0-100
    public double OverallProgress { get; set; } = 0; // 0-100
    
    public long FilesScanned { get; set; }
    public long DirectoriesScanned { get; set; }
    public long BytesAnalyzed { get; set; }
    public int AppsDiscovered { get; set; }
    public int RecommendationsGenerated { get; set; }
    
    public string EstimatedTimeRemaining { get; set; } = "Calculating...";
    public DateTime LastUpdate { get; set; } = DateTime.UtcNow;
}

public class DeepScanSummary
{
    public long TotalBytesScanned { get; set; }
    public long TotalFilesScanned { get; set; }
    public int TotalAppsFound { get; set; }
    public int TotalRecommendations { get; set; }
    
    public long PotentialSpaceSavings { get; set; }
    public int AppsRecommendedForRemoval { get; set; }
    public int FileClustersForRelocation { get; set; }
    public int CacheCleanupOpportunities { get; set; }
    public int DuplicateGroupsFound { get; set; }
    
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
