namespace SentinAI.Shared.Models.DeepScan;

/// <summary>
/// Represents space analysis for a single drive.
/// </summary>
public class DriveAnalysis
{
    public string DriveLetter { get; set; } = "";
    public string VolumeLabel { get; set; } = "";
    public DriveType DriveType { get; set; }
    
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public long UsedBytes => TotalBytes - FreeBytes;
    public double UsedPercentage => TotalBytes > 0 ? (UsedBytes * 100.0 / TotalBytes) : 0;
    
    public List<SpaceCategory> Categories { get; set; } = new();
    public List<LargeFile> LargestFiles { get; set; } = new();
    public List<DirectorySize> LargestDirectories { get; set; } = new();
    
    // Formatted properties
    public string TotalFormatted => FormatBytes(TotalBytes);
    public string FreeFormatted => FormatBytes(FreeBytes);
    public string UsedFormatted => FormatBytes(UsedBytes);
    
    public bool IsLowOnSpace => UsedPercentage > 90;
    public bool CanAcceptRelocations => FreeBytes > 10L * 1024 * 1024 * 1024; // >10GB free
    
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

public class SpaceCategory
{
    public string Name { get; set; } = "";
    public SpaceCategoryType Type { get; set; }
    public long Bytes { get; set; }
    public int FileCount { get; set; }
    public int DirectoryCount { get; set; }
    public double Percentage { get; set; }
    public string Color { get; set; } = "#888888"; // For UI visualization
    
    public string BytesFormatted => FormatBytes(Bytes);
    
    public List<SpaceCategory> SubCategories { get; set; } = new();
    
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

public enum SpaceCategoryType
{
    System,
    Applications,
    Games,
    UserDocuments,
    UserDownloads,
    UserMedia,
    UserDesktop,
    AppDataLocal,
    AppDataRoaming,
    ProgramData,
    TempFiles,
    BrowserCache,
    WindowsUpdate,
    SystemRestore,
    RecycleBin,
    Other
}

public class LargeFile
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Extension { get; set; } = "";
    public long Bytes { get; set; }
    public DateTime LastModified { get; set; }
    public DateTime LastAccessed { get; set; }
    public string? AssociatedApp { get; set; }
    public FileCategory Category { get; set; }
    
    public string BytesFormatted => FormatBytes(Bytes);
    public int DaysSinceAccess => (int)(DateTime.Now - LastAccessed).TotalDays;
    
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

public enum FileCategory
{
    Document,
    Image,
    Video,
    Audio,
    Archive,
    Executable,
    Database,
    GameAsset,
    CacheData,
    TempFile,
    SystemFile,
    Unknown
}

public class DirectorySize
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public long Bytes { get; set; }
    public int FileCount { get; set; }
    public int SubdirectoryCount { get; set; }
    public DateTime LastModified { get; set; }
    public string? AssociatedApp { get; set; }
    public SpaceCategoryType Category { get; set; }
    
    public string BytesFormatted => FormatBytes(Bytes);
    
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
