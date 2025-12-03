namespace SentinAI.Shared.Models.DeepScan;

/// <summary>
/// Information about a target drive for scanning/relocation.
/// </summary>
public class TargetDriveInfo
{
    public string Letter { get; set; } = "";
    public string Label { get; set; } = "";
    public long TotalSpace { get; set; }
    public long FreeSpace { get; set; }
    public long UsedSpace => TotalSpace - FreeSpace;
    public double UsedPercentage => TotalSpace > 0 ? (double)UsedSpace / TotalSpace * 100 : 0;
    public string DriveType { get; set; } = "";
    public bool IsReady { get; set; }

    public string TotalSpaceFormatted => FormatBytes(TotalSpace);
    public string FreeSpaceFormatted => FormatBytes(FreeSpace);
    public string UsedSpaceFormatted => FormatBytes(UsedSpace);

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
/// Options for configuring a deep scan.
/// </summary>
public class DeepScanOptions
{
    /// <summary>
    /// Drives to scan.
    /// </summary>
    public List<string> TargetDrives { get; set; } = new();

    /// <summary>
    /// Whether to scan installed applications.
    /// </summary>
    public bool ScanInstalledApps { get; set; } = true;

    /// <summary>
    /// Whether to scan for duplicate files.
    /// </summary>
    public bool ScanForDuplicates { get; set; } = true;

    /// <summary>
    /// Whether to include hidden files in scanning.
    /// </summary>
    public bool IncludeHiddenFiles { get; set; } = false;

    /// <summary>
    /// Whether to generate file relocation plans.
    /// </summary>
    public bool GenerateRelocationPlans { get; set; } = true;

    /// <summary>
    /// Paths to exclude from scanning.
    /// </summary>
    public List<string> ExcludePaths { get; set; } = new()
    {
        @"Windows",
        @"Program Files",
        @"Program Files (x86)",
        @"$Recycle.Bin",
        @"System Volume Information"
    };

    /// <summary>
    /// Minimum file size to consider (bytes).
    /// </summary>
    public long MinFileSizeBytes { get; set; } = 1024; // 1KB

    /// <summary>
    /// Maximum files to scan before stopping.
    /// </summary>
    public int MaxFilesToScan { get; set; } = 1_000_000;
}

/// <summary>
/// Drive analysis results.
/// </summary>
public class DriveAnalysisResult
{
    public string DriveLetter { get; set; } = "";
    public long TotalSpace { get; set; }
    public long UsedSpace { get; set; }
    public long FreeSpace { get; set; }

    // Breakdown by category
    public long SystemFilesBytes { get; set; }
    public long ApplicationsBytes { get; set; }
    public long DocumentsBytes { get; set; }
    public long MediaBytes { get; set; }
    public long TempFilesBytes { get; set; }
    public long OtherBytes { get; set; }

    // File counts
    public int TotalFiles { get; set; }
    public int TotalDirectories { get; set; }

    // Largest items
    public List<LargeItem> LargestFiles { get; set; } = new();
    public List<LargeItem> LargestDirectories { get; set; } = new();
}

/// <summary>
/// Represents a large file or directory.
/// </summary>
public class LargeItem
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public long SizeBytes { get; set; }
    public bool IsDirectory { get; set; }
    public DateTime LastModified { get; set; }

    public string SizeFormatted => FormatBytes(SizeBytes);

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
