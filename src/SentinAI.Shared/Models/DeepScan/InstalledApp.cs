namespace SentinAI.Shared.Models.DeepScan;

/// <summary>
/// Represents an installed application on the system.
/// </summary>
public class InstalledApp
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string? Version { get; set; }
    public string? InstallLocation { get; set; }
    public DateTime? InstallDate { get; set; }
    public DateTime? LastUsedDate { get; set; }

    /// <summary>
    /// Source of the app (Store, Desktop, System, etc.)
    /// </summary>
    public AppSource Source { get; set; } = AppSource.Unknown;

    /// <summary>
    /// Category of the application.
    /// </summary>
    public AppCategory Category { get; set; } = AppCategory.Unknown;

    // Size information
    public long InstallSizeBytes { get; set; }
    public long DataSizeBytes { get; set; }
    public long CacheSizeBytes { get; set; }
    public long TotalSizeBytes => InstallSizeBytes + DataSizeBytes + CacheSizeBytes;

    // Flags
    public bool IsSystemApp { get; set; }
    public bool IsBloatware { get; set; }
    public bool IsUnused { get; set; }
    public bool CanUninstall { get; set; } = true;
    public bool CanClearCache { get; set; }

    // Computed properties
    public int DaysSinceLastUse => LastUsedDate.HasValue
        ? (int)(DateTime.Now - LastUsedDate.Value).TotalDays
        : 365; // Default to a year if never used

    public string TotalSizeFormatted => FormatBytes(TotalSizeBytes);
    public string InstallSizeFormatted => FormatBytes(InstallSizeBytes);
    public string DataSizeFormatted => FormatBytes(DataSizeBytes);
    public string CacheSizeFormatted => FormatBytes(CacheSizeBytes);

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
/// Source of app installation.
/// </summary>
public enum AppSource
{
    Unknown,
    MicrosoftStore,
    Desktop,
    System,
    Sideloaded,
    Package
}

/// <summary>
/// Category of application.
/// </summary>
public enum AppCategory
{
    Unknown,
    Productivity,
    Development,
    Gaming,
    Media,
    Communication,
    Utility,
    Security,
    System,
    Browser,
    Social,
    Education,
    Finance,
    Other
}
