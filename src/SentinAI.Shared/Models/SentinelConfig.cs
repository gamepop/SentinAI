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
