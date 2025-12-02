namespace SentinAI.Shared.Models.DeepScan;

/// <summary>
/// Represents a group of related files that can be managed together.
/// </summary>
public class FileCluster
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string BasePath { get; set; } = "";
    public string Name { get; set; } = "";
    public FileClusterType Type { get; set; }
    
    public List<string> FilePaths { get; set; } = new();
    public int FileCount => FilePaths.Count;
    public long TotalBytes { get; set; }
    
    // File type distribution
    public Dictionary<string, int> FileTypeDistribution { get; set; } = new();
    public List<string> FileTypes => FileTypeDistribution.Keys.ToList();
    public string PrimaryFileType => FileTypeDistribution
        .OrderByDescending(x => x.Value)
        .FirstOrDefault().Key ?? "unknown";
    
    // Time information
    public DateTime OldestFile { get; set; }
    public DateTime NewestFile { get; set; }
    public DateTime LastModified { get; set; }
    public int DaysSinceModified => (int)(DateTime.Now - LastModified).TotalDays;
    
    // Association
    public string? AssociatedApp { get; set; }
    public string? AssociatedAppId { get; set; }
    public SpaceCategoryType Category { get; set; }
    
    // Relocation info
    public bool CanRelocate { get; set; }
    public bool RequiresJunction { get; set; }
    public List<TargetDriveInfo> AvailableDrives { get; set; } = new();
    
    // Formatted
    public string TotalSizeFormatted => FormatBytes(TotalBytes);
    
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

public enum FileClusterType
{
    AppCache,           // Browser cache, app cache
    AppData,            // Application data folders
    TempFiles,          // Temporary files
    Downloads,          // User downloads
    Documents,          // User documents
    MediaPhotos,        // Photo collections
    MediaVideos,        // Video files
    MediaMusic,         // Music files
    GameAssets,         // Game installation/data
    DeveloperArtifacts, // node_modules, bin/obj, etc.
    Archives,           // Compressed files
    Duplicates,         // Duplicate file groups
    OldFiles,           // Files not accessed in 1+ year
    LargeFiles,         // Individual large files
    Unknown
}

/// <summary>
/// Represents a group of duplicate files.
/// </summary>
public class DuplicateGroup
{
    public string Hash { get; set; } = "";
    public long FileSize { get; set; }
    public List<DuplicateFileEntry> Files { get; set; } = new();
    public int DuplicateCount => Files.Count - 1;
    public long WastedBytes => FileSize * DuplicateCount;
    
    public string FileSizeFormatted => FormatBytes(FileSize);
    public string WastedBytesFormatted => FormatBytes(WastedBytes);
    
    // The file to keep (most recently accessed, or in most important location)
    public DuplicateFileEntry? RecommendedKeep { get; set; }
    
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

public class DuplicateFileEntry
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime LastAccessed { get; set; }
    public DateTime LastModified { get; set; }
    public string DriveLetter { get; set; } = "";
    public int LocationPriority { get; set; } // Lower = more important location
}

/// <summary>
/// Simple drive info for relocation options.
/// </summary>
public class TargetDriveInfo
{
    public string Letter { get; set; } = "";
    public string Label { get; set; } = "";
    public long FreeBytes { get; set; }
    public long TotalBytes { get; set; }
    public string FreeSpaceFormatted => FormatBytes(FreeBytes);
    
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
