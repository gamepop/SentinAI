using System.Text.Json;
using Microsoft.Extensions.Logging;
using SentinAI.Shared.Models.DeepScan;

namespace SentinAI.Web.Services.DeepScan;

/// <summary>
/// Analyzes deep scan findings using AI with RAG memory context.
/// </summary>
public class DeepScanBrainAnalyzer
{
    private readonly ILogger<DeepScanBrainAnalyzer> _logger;
    private readonly AgentBrain _brain;
    private readonly IDeepScanRagStore _ragStore;
    
    public DeepScanBrainAnalyzer(
        ILogger<DeepScanBrainAnalyzer> logger,
        AgentBrain brain,
        IDeepScanRagStore ragStore)
    {
        _logger = logger;
        _brain = brain;
        _ragStore = ragStore;
    }
    
    /// <summary>
    /// Analyzes an app for removal recommendation with RAG context.
    /// </summary>
    public async Task<AppRemovalRecommendation> AnalyzeAppForRemovalAsync(
        InstalledApp app,
        CancellationToken ct = default)
    {
        _logger.LogDebug("Analyzing app for removal: {App}", app.Name);
        
        // Retrieve similar past decisions from RAG
        var similarDecisions = await _ragStore.FindSimilarAppDecisionsAsync(app);
        var publisherPatterns = await _ragStore.GetAppRemovalPatternsAsync(app.Publisher);
        
        // Use heuristics + learning patterns (AI prompt analysis will be added when brain supports it)
        var (shouldRemove, confidence, category, reason) = AnalyzeAppWithHeuristics(app, similarDecisions, publisherPatterns);
        
        // Adjust confidence based on learning
        confidence = AdjustConfidence(confidence, similarDecisions);
        
        var recommendation = new AppRemovalRecommendation
        {
            App = app,
            ShouldRemove = shouldRemove,
            Confidence = confidence,
            Category = category,
            AiReason = reason,
            CanUninstall = app.CanUninstall,
            CanClearData = app.DataSizeBytes > 0,
            CanClearCache = app.CanClearCache,
            UninstallSavings = app.InstallSizeBytes,
            DataClearSavings = app.DataSizeBytes,
            CacheClearSavings = app.CacheSizeBytes,
            SimilarPastDecisions = similarDecisions.Count,
            LearnedInfluence = BuildLearnedInfluenceText(similarDecisions, publisherPatterns)
        };
        
        return recommendation;
    }
    
    /// <summary>
    /// Analyzes files for relocation recommendation with RAG context.
    /// </summary>
    public async Task<RelocationRecommendation> AnalyzeFilesForRelocationAsync(
        FileCluster cluster,
        CancellationToken ct = default)
    {
        _logger.LogDebug("Analyzing files for relocation: {Path}", cluster.BasePath);
        
        // Retrieve similar past decisions
        var similarDecisions = await _ragStore.FindSimilarFileDecisionsAsync(cluster);
        var fileTypePattern = await _ragStore.GetRelocationPatternsAsync(cluster.PrimaryFileType);
        
        // Use heuristics + learning patterns
        var (shouldRelocate, priority, targetDrive, reason) = AnalyzeRelocationWithHeuristics(cluster, similarDecisions, fileTypePattern);
        
        var confidence = 0.7 + (similarDecisions.Count * 0.05); // Base + learning boost
        confidence = Math.Min(confidence, 0.95);
        
        var recommendation = new RelocationRecommendation
        {
            Cluster = cluster,
            ShouldRelocate = shouldRelocate && cluster.CanRelocate,
            Priority = priority,
            TargetDrive = targetDrive ?? cluster.AvailableDrives.FirstOrDefault()?.Letter,
            Confidence = confidence,
            RequiresJunction = cluster.RequiresJunction,
            AiReason = reason,
            SimilarPastDecisions = similarDecisions.Count,
            LearnedInfluence = BuildFileLearnedInfluenceText(similarDecisions, fileTypePattern)
        };
        
        return recommendation;
    }
    
    /// <summary>
    /// Analyzes a cleanup opportunity with RAG context.
    /// </summary>
    public async Task<CleanupOpportunity> AnalyzeCleanupOpportunityAsync(
        CleanupOpportunity opportunity,
        CancellationToken ct = default)
    {
        // Most cleanup opportunities are straightforward - use heuristics
        var reason = opportunity.Type switch
        {
            CleanupType.WindowsTemp => "[AI] Windows temp folder - safe to clean",
            CleanupType.UserTemp => "[AI] User temp folder - safe to clean",
            CleanupType.BrowserCache => $"[AI] {opportunity.AssociatedApp ?? "Browser"} cache - safe to clean",
            CleanupType.ThumbnailCache => "[AI] Thumbnail cache - safe to clean, will rebuild automatically",
            CleanupType.WindowsUpdateCache => "[AI] Windows Update downloads - safe to clean after updates installed",
            CleanupType.RecycleBin => "[AI] Recycle Bin - files already deleted by user",
            CleanupType.AppCache => $"[AI] {opportunity.AssociatedApp ?? "App"} cache - generally safe to clean",
            _ => "[AI] Analyzed for cleanup"
        };
        
        opportunity.AiReason = reason;
        opportunity.Confidence = opportunity.Risk == CleanupRisk.None ? 0.95 : 0.75;
        
        return await Task.FromResult(opportunity);
    }
    
    private (bool shouldRemove, double confidence, AppRemovalCategory category, string reason) AnalyzeAppWithHeuristics(
        InstalledApp app,
        List<DeepScanMemory> similarDecisions,
        AppRemovalPattern publisherPatterns)
    {
        // Check if it's bloatware
        if (app.IsBloatware)
        {
            return (true, 0.9, AppRemovalCategory.Bloatware, 
                "[AI] Detected as bloatware/pre-installed unnecessary software");
        }
        
        // Check if it's a system app
        if (app.IsSystemApp)
        {
            return (false, 0.95, AppRemovalCategory.KeepRecommended,
                "[AI] System app - required for Windows functionality");
        }
        
        // Check publisher patterns from learning
        if (publisherPatterns.TotalDecisions >= 3 && publisherPatterns.RemovalRate > 0.8)
        {
            return (true, 0.85, AppRemovalCategory.Bloatware,
                $"[AI] User typically removes apps from {app.Publisher} ({publisherPatterns.RemovalRate:P0} removal rate)");
        }
        
        // Check if unused (90+ days)
        if (app.IsUnused && app.DaysSinceLastUse > 90)
        {
            var confidence = app.DaysSinceLastUse > 180 ? 0.85 : 0.7;
            return (true, confidence, AppRemovalCategory.Unused,
                $"[AI] Not used in {app.DaysSinceLastUse} days");
        }
        
        // Large unused apps
        if (app.TotalSizeBytes > 1024L * 1024 * 1024 && app.IsUnused) // >1GB and unused
        {
            return (true, 0.75, AppRemovalCategory.LargeUnused,
                $"[AI] Large app ({app.TotalSizeFormatted}) not used in {app.DaysSinceLastUse} days");
        }
        
        // Default: optional, lean towards keeping
        return (false, 0.6, AppRemovalCategory.Optional,
            "[AI] App appears to be in use or recently accessed");
    }
    
    private (bool shouldRelocate, int priority, string? targetDrive, string reason) AnalyzeRelocationWithHeuristics(
        FileCluster cluster,
        List<DeepScanMemory> similarDecisions,
        FileRelocationPattern fileTypePattern)
    {
        if (!cluster.CanRelocate)
        {
            return (false, 1, null, "[AI] Files cannot be safely relocated");
        }
        
        // Check learned preferences
        if (fileTypePattern.TotalDecisions >= 2 && 
            fileTypePattern.RelocationRate > 0.7 &&
            !string.IsNullOrEmpty(fileTypePattern.PreferredTargetDrive))
        {
            var priority = cluster.TotalBytes > 10L * 1024 * 1024 * 1024 ? 4 : 3;
            return (true, priority, fileTypePattern.PreferredTargetDrive,
                $"[AI] User prefers {cluster.PrimaryFileType} files on {fileTypePattern.PreferredTargetDrive}");
        }
        
        // Prioritize by size
        int sizePriority;
        if (cluster.TotalBytes > 50L * 1024 * 1024 * 1024) // >50GB
            sizePriority = 5;
        else if (cluster.TotalBytes > 10L * 1024 * 1024 * 1024) // >10GB
            sizePriority = 4;
        else if (cluster.TotalBytes > 1L * 1024 * 1024 * 1024) // >1GB
            sizePriority = 3;
        else
            sizePriority = 2;
        
        // Determine reason based on cluster type
        var reason = cluster.Type switch
        {
            FileClusterType.MediaVideos => $"[AI] Large video files ({cluster.TotalSizeFormatted}) - good candidate for relocation",
            FileClusterType.MediaPhotos => $"[AI] Photo collection ({cluster.TotalSizeFormatted}) - can be moved to free space",
            FileClusterType.GameAssets => $"[AI] Game files ({cluster.TotalSizeFormatted}) - relocatable with junction",
            FileClusterType.Downloads => $"[AI] Downloads folder ({cluster.TotalSizeFormatted}) - consider organizing/relocating",
            FileClusterType.Archives => $"[AI] Archive files ({cluster.TotalSizeFormatted}) - safe to relocate",
            FileClusterType.OldFiles => $"[AI] Old unused files ({cluster.TotalSizeFormatted}) - consider archiving",
            _ => $"[AI] Files ({cluster.TotalSizeFormatted}) can be relocated to free space"
        };
        
        var targetDrive = cluster.AvailableDrives.FirstOrDefault()?.Letter;
        
        return (targetDrive != null, sizePriority, targetDrive, reason);
    }
    
    private double AdjustConfidence(double baseConfidence, List<DeepScanMemory> similarDecisions)
    {
        if (!similarDecisions.Any())
            return baseConfidence;
        
        // Calculate agreement rate with past decisions
        var agreementRate = similarDecisions.Count(m => m.UserAgreed) / (double)similarDecisions.Count;
        
        // Adjust confidence: if AI was often wrong, reduce; if often right, boost
        var adjustment = (agreementRate - 0.5) * 0.2; // ±10% max adjustment
        
        return Math.Clamp(baseConfidence + adjustment, 0.1, 0.95);
    }
    
    private string? BuildLearnedInfluenceText(List<DeepScanMemory> memories, AppRemovalPattern pattern)
    {
        if (!memories.Any() && pattern.TotalDecisions == 0)
            return null;
        
        var parts = new List<string>();
        
        if (memories.Any())
        {
            parts.Add($"Based on {memories.Count} similar past decision(s)");
        }
        
        if (pattern.TotalDecisions > 0)
        {
            parts.Add($"Publisher removal rate: {pattern.RemovalRate:P0}");
        }
        
        return string.Join(". ", parts);
    }
    
    private string? BuildFileLearnedInfluenceText(List<DeepScanMemory> memories, FileRelocationPattern pattern)
    {
        if (!memories.Any() && pattern.TotalDecisions == 0)
            return null;
        
        var parts = new List<string>();
        
        if (memories.Any())
        {
            parts.Add($"Based on {memories.Count} similar file decision(s)");
        }
        
        if (pattern.TotalDecisions > 0 && !string.IsNullOrEmpty(pattern.PreferredTargetDrive))
        {
            parts.Add($"User prefers {pattern.PreferredTargetDrive} for {pattern.FileType} files");
        }
        
        return string.Join(". ", parts);
    }
}
