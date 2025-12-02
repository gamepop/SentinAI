namespace SentinAI.Shared.Models.DeepScan;

/// <summary>
/// Represents an installed application with usage and size information.
/// </summary>
public class InstalledApp
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Version { get; set; } = "";
    public string InstallPath { get; set; } = "";
    public DateTime? InstallDate { get; set; }
    public DateTime? LastAccessed { get; set; }
    
    public AppSource Source { get; set; }
    public AppCategory Category { get; set; }
    
    // Size information
    public long InstallSizeBytes { get; set; }
    public long DataSizeBytes { get; set; }
    public long CacheSizeBytes { get; set; }
    public long TotalSizeBytes => InstallSizeBytes + DataSizeBytes + CacheSizeBytes;
    
    // Associated paths
    public List<string> DataFolders { get; set; } = new();
    public List<string> CacheFolders { get; set; } = new();
    public string? RegistryKey { get; set; }
    public string? UninstallCommand { get; set; }
    
    // Usage metrics
    public int DaysSinceLastUse => LastAccessed.HasValue 
        ? (int)(DateTime.Now - LastAccessed.Value).TotalDays 
        : -1;
    public bool IsUnused => DaysSinceLastUse > 90;
    public bool IsBloatware { get; set; }
    public bool IsSystemApp { get; set; }
    public bool CanUninstall => !IsSystemApp && !string.IsNullOrEmpty(UninstallCommand);
    public bool CanClearCache => CacheSizeBytes > 0 && CacheFolders.Any();
    public bool CanRelocate { get; set; }
    
    // Formatted properties
    public string InstallSizeFormatted => FormatBytes(InstallSizeBytes);
    public string DataSizeFormatted => FormatBytes(DataSizeBytes);
    public string CacheSizeFormatted => FormatBytes(CacheSizeBytes);
    public string TotalSizeFormatted => FormatBytes(TotalSizeBytes);
    public string LastAccessedFormatted => LastAccessed?.ToString("MMM dd, yyyy") ?? "Unknown";
    public string InstallDateFormatted => InstallDate?.ToString("MMM dd, yyyy") ?? "Unknown";
    
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

public enum AppSource
{
    Registry,           // Traditional installer (MSI, EXE)
    MicrosoftStore,     // UWP/MSIX apps
    Steam,
    EpicGames,
    GOG,
    Portable,           // Portable apps (no installer)
    Unknown
}

public enum AppCategory
{
    Productivity,
    Development,
    Communication,
    Media,
    Gaming,
    Utility,
    Security,
    System,
    Browser,
    Education,
    Business,
    Bloatware,
    Unknown
}

/// <summary>
/// Known bloatware publishers and app patterns for detection.
/// </summary>
public static class BloatwarePatterns
{
    public static readonly HashSet<string> KnownBloatwarePublishers = new(StringComparer.OrdinalIgnoreCase)
    {
        "McAfee",
        "Norton",
        "WildTangent",
        "Candy Crush",
        "King.com",
        "ByteDance",
        "Facebook",
        "Spotify AB" // Pre-installed Spotify
    };
    
    public static readonly HashSet<string> KnownBloatwarePatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "Candy Crush",
        "Farm Heroes",
        "Bubble Witch",
        "Hidden City",
        "March of Empires",
        "Phototastic Collage",
        "PicsArt",
        "TikTok",
        "Instagram",
        "Disney+",
        "Netflix", // Pre-installed
        "HP Wolf Security",
        "HP Support",
        "Dell SupportAssist",
        "Lenovo Vantage",
        "ASUS",
        "Acer Care Center"
    };
    
    public static readonly HashSet<string> EssentialApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft Edge",
        "Windows Security",
        "Windows Defender",
        "Microsoft Store",
        ".NET",
        "Visual C++",
        "DirectX"
    };
}
