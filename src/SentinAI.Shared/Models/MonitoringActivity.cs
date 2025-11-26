namespace SentinAI.Shared.Models;

/// <summary>
/// Represents a monitoring activity event
/// </summary>
public class MonitoringActivity
{
    /// <summary>
    /// Unique identifier for the activity
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Type of the monitoring activity
    /// </summary>
    public MonitoringActivityType Type { get; set; }

    /// <summary>
    /// Scope of the activity (e.g., folder path)
    /// </summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>
    /// Drive letter if applicable
    /// </summary>
    public string Drive { get; set; } = string.Empty;

    /// <summary>
    /// Current state of the activity
    /// </summary>
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable message about the activity
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp of the activity
    /// </summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Additional metadata about the activity
    /// </summary>
    public Dictionary<string, string>? Metadata { get; set; }
}

/// <summary>
/// Types of monitoring activities
/// </summary>
public enum MonitoringActivityType
{
    /// <summary>
    /// File system change detected
    /// </summary>
    FileChange,

    /// <summary>
    /// Analysis started
    /// </summary>
    AnalysisStart,

    /// <summary>
    /// Analysis completed
    /// </summary>
    AnalysisComplete,

    /// <summary>
    /// Cleanup executed
    /// </summary>
    CleanupExecuted,

    /// <summary>
    /// Error occurred
    /// </summary>
    Error,

    /// <summary>
    /// Service status change
    /// </summary>
    ServiceStatus,

    /// <summary>
    /// Drive sweep operation
    /// </summary>
    DriveSweep,

    /// <summary>
    /// USN Journal batch processing
    /// </summary>
    UsnBatch,

    /// <summary>
    /// State machine transition
    /// </summary>
    StateTransition,

    /// <summary>
    /// Cleanup analysis operation
    /// </summary>
    CleanupAnalysis,

    /// <summary>
    /// Interaction with the AI model
    /// </summary>
    ModelInteraction,

    /// <summary>
    /// Connection status with the Brain service
    /// </summary>
    BrainConnection,

    /// <summary>
    /// Custom activity type
    /// </summary>
    Custom
}
