using SentinAI.Shared;

namespace SentinAI.Shared.Models;

/// <summary>
/// States in the Propose-Verify-Execute state machine
/// </summary>
public enum AgentState
{
    Idle,       // Monitoring via USN Journal
    Triage,     // Detecting patterns (node_modules >60 days, %TEMP% >5GB)
    Proposal,   // AI drafts cleanup plan (JSON)
    Approval,   // Auto-approve or User-review
    Execution,  // Sentinel Service deletes files
    Report      // Update UI with results
}

/// <summary>
/// Represents an analysis session as it moves through the state machine
/// </summary>
public class AnalysisSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public AgentState CurrentState { get; set; } = AgentState.Idle;
    public List<FileEvent> TriggerEvents { get; set; } = new();
    public List<CleanupSuggestion> Suggestions { get; set; } = new();
    public string Scope { get; set; } = string.Empty;
    public bool RequiresUserApproval { get; set; }
    public bool UserApproved { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
}

