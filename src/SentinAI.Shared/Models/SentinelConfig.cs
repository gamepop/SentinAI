namespace SentinAI.Shared.Models;

/// <summary>
/// Configuration for the SentinAI application
/// </summary>
public class SentinelConfig
{
    /// <summary>
    /// Whether to auto-start with Windows
    /// </summary>
    public bool AutoStart { get; set; } = false;

    /// <summary>
    /// Whether to start minimized
    /// </summary>
    public bool StartMinimized { get; set; } = false;

    /// <summary>
    /// Whether to show notifications
    /// </summary>
    public bool ShowNotifications { get; set; } = true;

    /// <summary>
    /// Minimum confidence level for auto-approval (0.0 - 1.0)
    /// </summary>
    public double AutoApproveMinConfidence { get; set; } = 0.95;

    /// <summary>
    /// Whether to enable automatic cleanup
    /// </summary>
    public bool EnableAutoCleanup { get; set; } = false;

    /// <summary>
    /// Whether AI Auto Mode is enabled (autonomous decisions)
    /// </summary>
    public bool AiAutoModeEnabled { get; set; } = false;

    /// <summary>
    /// Confidence threshold for AI decisions (0.0 - 1.0)
    /// </summary>
    public double AiConfidenceThreshold { get; set; } = 0.85;

    /// <summary>
    /// Categories to include in AI auto-cleanup
    /// </summary>
    public List<string> AiAutoCleanCategories { get; set; } = new();

    /// <summary>
    /// The drive letter to monitor (e.g., "C")
    /// </summary>
    public string MonitoredDrive { get; set; } = "C";

    /// <summary>
    /// Seconds to wait before processing file changes
    /// </summary>
    public int DebounceSeconds { get; set; } = 5;

    /// <summary>
    /// Maximum size of file to analyze in MB
    /// </summary>
    public int MaxAnalysisSizeMB { get; set; } = 100;

    /// <summary>
    /// Days before a file is considered "old"
    /// </summary>
    public int OldFileThresholdDays { get; set; } = 30;

    /// <summary>
    /// Days before node_modules are considered for cleanup
    /// </summary>
    public int NodeModulesThresholdDays { get; set; } = 14;

    /// <summary>
    /// Threshold for heavy write detection in MB
    /// </summary>
    public int HeavyWriteThresholdMB { get; set; } = 1024;

    /// <summary>
    /// File patterns to auto-approve for cleanup
    /// </summary>
    public List<string> AutoApprovePatterns { get; set; } = new();

    /// <summary>
    /// Folders to exclude from monitoring
    /// </summary>
    public List<string> ExcludedFolders { get; set; } = new();

    /// <summary>
    /// Paths to exclude from scanning
    /// </summary>
    public List<string> ExcludedPaths { get; set; } = new();

    /// <summary>
    /// Categories to include in automatic cleanup
    /// </summary>
    public List<string> AutoCleanupCategories { get; set; } = new()
    {
        CleanupCategories.Temp,
        CleanupCategories.Cache
    };

    /// <summary>
    /// Whether the RAG memory system is enabled
    /// </summary>
    public bool RagEnabled { get; set; } = true;

    /// <summary>
    /// The AI execution provider (CPU or DirectML)
    /// </summary>
    public string ExecutionProvider { get; set; } = "CPU";
}
