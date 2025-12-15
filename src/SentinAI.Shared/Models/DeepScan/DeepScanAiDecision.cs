namespace SentinAI.Shared.Models.DeepScan;

/// <summary>
/// AI decision result from the Phi-4 Mini model for Deep Scan analysis.
/// </summary>
public class DeepScanAiDecision
{
    /// <summary>
    /// Whether the AI recommends proceeding with the action (removal/relocation).
    /// </summary>
    public bool ShouldProceed { get; set; }

    /// <summary>
    /// AI confidence level (0.0 - 1.0).
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>
    /// AI-generated reason for the decision.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Category classification (for app removal: Bloatware, Unused, LargeUnused, KeepRecommended, Optional).
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// Recommended priority level (1-5, for relocation decisions).
    /// </summary>
    public int? Priority { get; set; }

    /// <summary>
    /// Recommended target drive letter (for relocation decisions).
    /// </summary>
    public string? TargetDrive { get; set; }

    /// <summary>
    /// Whether this decision was made by the AI model or fell back to heuristics.
    /// </summary>
    public bool IsAiDecision { get; set; }
}
