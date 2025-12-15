namespace SentinAI.Shared.Models.DeepScan;

/// <summary>
/// Represents a cluster of related files that can be relocated together.
/// </summary>
public class FileCluster
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string BasePath { get; set; } = "";
    public FileClusterType Type { get; set; }
    public string PrimaryFileType { get; set; } = "";

    // Actual file paths in this cluster (for safe relocation)
    public List<string> FilePaths { get; set; } = new();

    // Size and count
    public long TotalBytes { get; set; }
    public int FileCount { get; set; }
    public int DirectoryCount { get; set; }

    // Status
    public bool CanRelocate { get; set; } = true;
    public bool RequiresJunction { get; set; }
    public string? JunctionReason { get; set; }

    // Available targets
    public List<AvailableDrive> AvailableDrives { get; set; } = new();

    // Formatted properties
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

/// <summary>
/// Type of file cluster.
/// </summary>
public enum FileClusterType
{
    Unknown,
    MediaVideos,
    MediaPhotos,
    MediaMusic,
    GameAssets,
    GameInstallation,
    Downloads,
    Documents,
    Archives,
    SourceCode,
    VirtualMachines,
    Backups,
    OldFiles,
    TempFiles,
    Other
}

/// <summary>
/// Information about an available drive for relocation.
/// </summary>
public class AvailableDrive
{
    public string Letter { get; set; } = "";
    public string Label { get; set; } = "";
    public long FreeSpace { get; set; }
    public long TotalSpace { get; set; }
    public bool IsRecommended { get; set; }

    public string FreeSpaceFormatted => FormatBytes(FreeSpace);

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
