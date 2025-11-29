namespace SentinAI.Shared.Models;

/// <summary>
/// Represents a suggestion for cleaning up a file or folder
/// </summary>
public class CleanupSuggestion
{
    /// <summary>
    /// Path to the file or folder to clean
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Size of the file/folder in bytes
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Category of the cleanup item (e.g., Temp, Cache, Downloads)
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// Whether it's safe to delete this item
    /// </summary>
    public bool SafeToDelete { get; set; }

    /// <summary>
    /// Human-readable reason for the decision
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Confidence score (0.0 - 1.0) for the decision
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>
    /// Whether the item can be auto-approved without user confirmation
    /// </summary>
    public bool AutoApprove { get; set; }
}
