using Microsoft.Extensions.Logging;
using SentinAI.Shared.Models.DeepScan;

namespace SentinAI.Web.Services.DeepScan;

/// <summary>
/// Service for recording user decisions and feeding them back into the learning system.
/// This creates a feedback loop where user decisions improve future recommendations.
/// </summary>
public class DeepScanLearningService
{
    private readonly ILogger<DeepScanLearningService> _logger;
    private readonly IDeepScanRagStore _ragStore;

    public DeepScanLearningService(
        ILogger<DeepScanLearningService> logger,
        IDeepScanRagStore ragStore)
    {
        _logger = logger;
        _ragStore = ragStore;
    }

    /// <summary>
    /// Records a user's decision on an app removal recommendation.
    /// </summary>
    public async Task RecordAppDecisionAsync(
        AppRemovalRecommendation recommendation,
        bool userApproved,
        string? userReason = null)
    {
        _logger.LogInformation(
            "Recording app decision for {AppName}: User {Decision}",
            recommendation.App?.Name,
            userApproved ? "approved" : "rejected");

        var memory = new DeepScanMemory
        {
            Type = DeepScanMemoryType.AppRemovalDecision,
            Context = BuildAppContext(recommendation),
            Decision = userApproved ? "approved" : "rejected",
            UserAgreed = userApproved,
            AiConfidence = recommendation.Confidence,
            AiReasoning = recommendation.AiReason,
            Metadata = new Dictionary<string, string>
            {
                { "appName", recommendation.App?.Name ?? "Unknown" },
                { "publisher", recommendation.App?.Publisher ?? "Unknown" },
                { "size_bytes", recommendation.TotalPotentialSavings.ToString() },
                { "daysSinceLastUse", recommendation.App?.DaysSinceLastUse.ToString() ?? "0" },
                { "userReason", userReason ?? "" },
                { "category", recommendation.App?.Category.ToString() ?? "" }
            }
        };

        await _ragStore.StoreMemoryAsync(memory);

        // Also store as a pattern learning if this was a correction
        if (!userApproved && recommendation.Confidence > 0.7)
        {
            await StoreCorrectionPatternAsync(recommendation, userReason);
        }
    }

    /// <summary>
    /// Records a user's decision on a file relocation recommendation.
    /// </summary>
    public async Task RecordRelocationDecisionAsync(
        RelocationRecommendation recommendation,
        bool userApproved,
        string? actualTargetDrive = null)
    {
        _logger.LogInformation(
            "Recording relocation decision for {ClusterName}: User {Decision}",
            recommendation.Cluster?.Name,
            userApproved ? "approved" : "rejected");

        var memory = new DeepScanMemory
        {
            Type = DeepScanMemoryType.RelocationDecision,
            Context = BuildRelocationContext(recommendation),
            Decision = userApproved ? "approved" : "rejected",
            UserAgreed = userApproved,
            AiConfidence = recommendation.Confidence,
            AiReasoning = recommendation.AiReason,
            Metadata = new Dictionary<string, string>
            {
                { "clusterType", recommendation.Cluster?.Type.ToString() ?? "" },
                { "clusterName", recommendation.Cluster?.Name ?? "" },
                { "sourcePath", recommendation.Cluster?.BasePath ?? "" },
                { "targetDrive", recommendation.TargetDrive ?? "" },
                { "actualTargetDrive", actualTargetDrive ?? recommendation.TargetDrive ?? "" },
                { "size_bytes", recommendation.Cluster?.TotalBytes.ToString() ?? "0" },
                { "fileCount", recommendation.Cluster?.FileCount.ToString() ?? "0" }
            }
        };

        await _ragStore.StoreMemoryAsync(memory);

        // Learn from target drive preference
        if (userApproved && !string.IsNullOrEmpty(actualTargetDrive))
        {
            await StoreTargetDrivePreferenceAsync(recommendation, actualTargetDrive);
        }
    }

    /// <summary>
    /// Records a user's decision on a cleanup opportunity.
    /// </summary>
    public async Task RecordCleanupDecisionAsync(
        CleanupOpportunity opportunity,
        bool userApproved)
    {
        _logger.LogInformation(
            "Recording cleanup decision for {OpportunityType}: User {Decision}",
            opportunity.Type,
            userApproved ? "approved" : "rejected");

        var memory = new DeepScanMemory
        {
            Type = DeepScanMemoryType.CleanupDecision,
            Context = BuildCleanupContext(opportunity),
            Decision = userApproved ? "approved" : "rejected",
            UserAgreed = userApproved,
            AiConfidence = opportunity.Confidence,
            AiReasoning = opportunity.AiReason,
            Metadata = new Dictionary<string, string>
            {
                { "cleanupType", opportunity.Type.ToString() },
                { "targetPath", opportunity.Path },
                { "description", opportunity.Description },
                { "size_bytes", opportunity.Bytes.ToString() },
                { "fileCount", opportunity.FileCount.ToString() },
                { "risk", opportunity.Risk.ToString() }
            }
        };

        await _ragStore.StoreMemoryAsync(memory);
    }

    /// <summary>
    /// Records user preference for future recommendations.
    /// </summary>
    public async Task RecordUserPreferenceAsync(
        string preferenceCategory,
        string preferenceKey,
        object preferenceValue,
        string? reason = null)
    {
        _logger.LogInformation(
            "Recording user preference: {Category}.{Key} = {Value}",
            preferenceCategory, preferenceKey, preferenceValue);

        var memory = new DeepScanMemory
        {
            Type = DeepScanMemoryType.UserPreference,
            Context = $"User preference for {preferenceCategory}: {preferenceKey}",
            Decision = preferenceValue?.ToString() ?? "null",
            UserAgreed = true, // Preferences are always user-initiated
            AiConfidence = 1.0, // Preferences are definitive
            Metadata = new Dictionary<string, string>
            {
                { "category", preferenceCategory },
                { "key", preferenceKey },
                { "value", preferenceValue?.ToString() ?? "null" },
                { "reason", reason ?? "" }
            }
        };

        await _ragStore.StoreMemoryAsync(memory);
    }

    /// <summary>
    /// Learns a file pattern from user decision.
    /// </summary>
    public async Task LearnFilePatternAsync(
        string fileExtension,
        string category,
        string action,
        bool userApproved,
        string? context = null)
    {
        _logger.LogInformation(
            "Learning file pattern: {Extension} -> {Category}/{Action} ({Approved})",
            fileExtension, category, action, userApproved);

        var memory = new DeepScanMemory
        {
            Type = DeepScanMemoryType.FilePatternLearning,
            Context = $"File pattern {fileExtension} in context: {context}",
            Decision = action,
            UserAgreed = userApproved,
            AiConfidence = userApproved ? 0.8 : 0.3,
            Metadata = new Dictionary<string, string>
            {
                { "file_types", fileExtension },
                { "category", category },
                { "action", action },
                { "patternContext", context ?? "" }
            }
        };

        await _ragStore.StoreMemoryAsync(memory);
    }

    /// <summary>
    /// Learns an app category from user decision.
    /// </summary>
    public async Task LearnAppCategoryAsync(
        string appName,
        string publisher,
        string category,
        bool isEssential)
    {
        _logger.LogInformation(
            "Learning app category: {AppName} -> {Category} (Essential: {IsEssential})",
            appName, category, isEssential);

        var memory = new DeepScanMemory
        {
            Type = DeepScanMemoryType.AppCategoryLearning,
            Context = $"App {appName} by {publisher} categorized as {category}",
            Decision = isEssential ? "essential" : "non-essential",
            UserAgreed = true,
            AiConfidence = 0.9, // User-confirmed categorization
            Metadata = new Dictionary<string, string>
            {
                { "appName", appName },
                { "publisher", publisher },
                { "category", category },
                { "isEssential", isEssential.ToString() }
            }
        };

        await _ragStore.StoreMemoryAsync(memory);
    }

    #region Helper Methods

    private string BuildAppContext(AppRemovalRecommendation recommendation)
    {
        var app = recommendation.App;
        if (app == null) return "Unknown app removal recommendation";

        var context = new System.Text.StringBuilder();
        context.AppendLine($"App: {app.Name} by {app.Publisher}");
        context.AppendLine($"Category: {app.Category}");
        context.AppendLine($"Size: {FormatSize(app.TotalSizeBytes)}");
        context.AppendLine($"Last used: {app.DaysSinceLastUse} days ago");
        context.AppendLine($"Source: {app.Source}");
        context.AppendLine($"AI Reason: {recommendation.AiReason}");

        return context.ToString();
    }

    private string BuildRelocationContext(RelocationRecommendation recommendation)
    {
        var cluster = recommendation.Cluster;
        if (cluster == null) return "Unknown relocation recommendation";

        var context = new System.Text.StringBuilder();
        context.AppendLine($"Cluster: {cluster.Name}");
        context.AppendLine($"Type: {cluster.Type}");
        context.AppendLine($"Path: {cluster.BasePath}");
        context.AppendLine($"Size: {FormatSize(cluster.TotalBytes)}");
        context.AppendLine($"Files: {cluster.FileCount}");
        context.AppendLine($"Target: {recommendation.TargetDrive}");
        context.AppendLine($"AI Reason: {recommendation.AiReason}");

        return context.ToString();
    }

    private string BuildCleanupContext(CleanupOpportunity opportunity)
    {
        var context = new System.Text.StringBuilder();
        context.AppendLine($"Cleanup type: {opportunity.Type}");
        context.AppendLine($"Target: {opportunity.Path}");
        context.AppendLine($"Description: {opportunity.Description}");
        context.AppendLine($"Space recovery: {FormatSize(opportunity.Bytes)}");
        context.AppendLine($"Files affected: {opportunity.FileCount}");
        context.AppendLine($"Risk: {opportunity.Risk}");
        context.AppendLine($"AI Reason: {opportunity.AiReason}");

        return context.ToString();
    }

    private async Task StoreCorrectionPatternAsync(
        AppRemovalRecommendation recommendation,
        string? userReason)
    {
        // When user rejects a high-confidence recommendation, learn from it
        var correctionMemory = new DeepScanMemory
        {
            Type = DeepScanMemoryType.CorrectionPattern,
            Context = $"CORRECTION: AI wrongly recommended removing {recommendation.App?.Name}",
            Decision = "do_not_remove",
            UserAgreed = true, // This is a correction, not a disagreement
            AiConfidence = recommendation.Confidence,
            AiReasoning = recommendation.AiReason,
            Metadata = new Dictionary<string, string>
            {
                { "appName", recommendation.App?.Name ?? "" },
                { "publisher", recommendation.App?.Publisher ?? "" },
                { "originalReason", recommendation.AiReason ?? "" },
                { "userCorrection", userReason ?? "" },
                { "isCorrection", "true" }
            }
        };

        await _ragStore.StoreMemoryAsync(correctionMemory);
        _logger.LogWarning(
            "AI correction recorded: {AppName} should NOT be removed. User reason: {Reason}",
            recommendation.App?.Name,
            userReason);
    }

    private async Task StoreTargetDrivePreferenceAsync(
        RelocationRecommendation recommendation,
        string actualTargetDrive)
    {
        if (recommendation.TargetDrive == actualTargetDrive)
            return; // No preference to learn

        var preferenceMemory = new DeepScanMemory
        {
            Type = DeepScanMemoryType.UserPreference,
            Context = $"User prefers {actualTargetDrive} over {recommendation.TargetDrive} for {recommendation.Cluster?.Type}",
            Decision = actualTargetDrive,
            UserAgreed = true,
            AiConfidence = 0.9,
            Metadata = new Dictionary<string, string>
            {
                { "clusterType", recommendation.Cluster?.Type.ToString() ?? "" },
                { "suggestedDrive", recommendation.TargetDrive ?? "" },
                { "preferredDrive", actualTargetDrive },
                { "preferenceType", "target_drive" }
            }
        };

        await _ragStore.StoreMemoryAsync(preferenceMemory);
        _logger.LogInformation(
            "Drive preference learned: {ClusterType} -> {PreferredDrive} (was: {Suggested})",
            recommendation.Cluster?.Type,
            actualTargetDrive,
            recommendation.TargetDrive);
    }

    private string FormatSize(long bytes)
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

    #endregion
}
