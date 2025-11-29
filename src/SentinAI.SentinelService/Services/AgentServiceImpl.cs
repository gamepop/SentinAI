using System.Linq;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using SentinAI.Shared;
using SentinAI.Shared.Models;

namespace SentinAI.SentinelService.Services;

/// <summary>
/// gRPC service implementation for communication with the Web UI and Brain
/// </summary>
public class AgentServiceImpl : AgentService.AgentServiceBase
{
    private readonly ILogger<AgentServiceImpl> _logger;
    private readonly ICleanupExecutor _cleanupExecutor;
    private readonly IMonitoringActivityStore _activityStore;
    private readonly IStateMachineOrchestrator _orchestrator;

    public AgentServiceImpl(
        ILogger<AgentServiceImpl> logger,
        ICleanupExecutor cleanupExecutor,
        IMonitoringActivityStore activityStore,
        IStateMachineOrchestrator orchestrator)
    {
        _logger = logger;
        _cleanupExecutor = cleanupExecutor;
        _activityStore = activityStore;
        _orchestrator = orchestrator;
    }

    public override Task<AnalysisResponse> RequestAnalysis(
        AnalysisRequest request,
        ServerCallContext context)
    {
        _logger.LogInformation(
            "Analysis requested for {Count} events. Reason: {Reason}",
            request.Events.Count,
            request.TriggerReason);

        // Generate unique analysis ID
        var analysisId = Guid.NewGuid().ToString();

        // Determine if user review is needed based on event patterns
        bool requiresReview = request.Events.Any(e =>
            !e.FilePath.Contains("temp", StringComparison.OrdinalIgnoreCase) &&
            !e.FilePath.Contains("cache", StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(new AnalysisResponse
        {
            RequiresUserReview = requiresReview,
            AnalysisId = analysisId
        });
    }

    /// <summary>
    /// Trigger on-demand analysis of specific folders
    /// </summary>
    public override async Task<TriggerAnalysisResponse> TriggerAnalysis(
        TriggerAnalysisRequest request,
        ServerCallContext context)
    {
        _logger.LogInformation(
            "🔍 TriggerAnalysis RPC: FullSystemScan={FullScan}, Folders={Count}, Reason={Reason}",
            request.FullSystemScan,
            request.FolderPaths.Count,
            request.Reason);

        var folderPaths = request.FolderPaths.ToList();

        // If full system scan, add common cleanup targets
        if (request.FullSystemScan)
        {
            var systemFolders = GetSystemCleanupFolders();
            folderPaths.AddRange(systemFolders);
            _logger.LogInformation("📂 Full system scan: added {Count} system folders", systemFolders.Count);
        }

        if (folderPaths.Count == 0)
        {
            return new TriggerAnalysisResponse
            {
                Accepted = false,
                Message = "No folders specified for analysis"
            };
        }

        var (accepted, analysisId, foldersQueued) = await _orchestrator.AnalyzeNowAsync(
            folderPaths,
            request.Reason ?? "Manual analysis",
            context.CancellationToken);

        return new TriggerAnalysisResponse
        {
            Accepted = accepted,
            AnalysisId = analysisId,
            FoldersQueued = foldersQueued,
            Message = accepted
                ? $"Analysis started for {foldersQueued} folder(s)"
                : "No files found in specified folders"
        };
    }

    /// <summary>
    /// Get pending suggestions awaiting user approval
    /// </summary>
    public override async Task<PendingSuggestionsResponse> GetPendingSuggestions(
        PendingSuggestionsRequest request,
        ServerCallContext context)
    {
        var pending = await _orchestrator.GetPendingAnalysesAsync();
        var response = new PendingSuggestionsResponse();

        foreach (var session in pending)
        {
            var analysis = new PendingAnalysis
            {
                AnalysisId = session.Id,
                TotalBytes = session.Suggestions.Sum(s => s.SizeBytes),
                CreatedAt = session.CreatedAt.ToString("o"),
                Scope = session.Scope ?? "Unknown"
            };

            foreach (var suggestion in session.Suggestions)
            {
                analysis.Suggestions.Add(new CleanupItem
                {
                    FilePath = suggestion.FilePath,
                    SizeBytes = suggestion.SizeBytes,
                    Category = suggestion.Category,
                    SafeToDelete = suggestion.SafeToDelete,
                    Reason = suggestion.Reason,
                    AutoApprove = suggestion.AutoApprove,
                    Confidence = suggestion.Confidence
                });
            }

            response.Analyses.Add(analysis);
        }

        _logger.LogInformation("📋 GetPendingSuggestions: {Count} pending analyses", response.Analyses.Count);
        return response;
    }

    private List<string> GetSystemCleanupFolders()
    {
        var folders = new List<string>();

        // Windows Temp
        var windowsTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        if (Directory.Exists(windowsTemp)) folders.Add(windowsTemp);

        // User Temp
        var userTemp = Path.GetTempPath();
        if (Directory.Exists(userTemp)) folders.Add(userTemp);

        // Browser Caches
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var chromeCacheParent = Path.Combine(localAppData, "Google", "Chrome", "User Data");
        if (Directory.Exists(chromeCacheParent))
        {
            var chromeCache = Path.Combine(chromeCacheParent, "Default", "Cache");
            if (Directory.Exists(chromeCache)) folders.Add(chromeCache);
        }

        var edgeCacheParent = Path.Combine(localAppData, "Microsoft", "Edge", "User Data");
        if (Directory.Exists(edgeCacheParent))
        {
            var edgeCache = Path.Combine(edgeCacheParent, "Default", "Cache");
            if (Directory.Exists(edgeCache)) folders.Add(edgeCache);
        }

        // Downloads folder (for old files)
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) folders.Add(downloads);

        return folders;
    }

    public override Task<CleanupSuggestions> GetCleanupSuggestions(
        CleanupRequest request,
        ServerCallContext context)
    {
        _logger.LogInformation(
            "Cleanup suggestions requested for: {Folder}",
            request.FolderPath);

        // This would normally call the AI Brain
        // For now, return placeholder response
        return Task.FromResult(new CleanupSuggestions
        {
            TotalBytesToFree = 0,
            Reasoning = "AI analysis not yet implemented"
        });
    }

    public override async Task<CleanupResult> ExecuteCleanup(
        CleanupCommand request,
        ServerCallContext context)
    {
        _logger.LogInformation(
            "🧹 Cleanup execution requested. Analysis ID: {AnalysisId}, FilePaths: {Count}, UserApproved: {Approved}",
            request.AnalysisId,
            request.FilePaths.Count,
            request.UserApproved);

        if (!request.UserApproved)
        {
            _logger.LogWarning("Cleanup rejected - user did not approve");
            return new CleanupResult
            {
                Success = false,
                Errors = { "User approval required" }
            };
        }

        // If AnalysisId is provided, use the orchestrator to get file paths from the session
        if (!string.IsNullOrWhiteSpace(request.AnalysisId))
        {
            List<string> filePathsToClean;
            bool isFullSessionCleanup = false;

            // If specific files are requested, use them (Partial Cleanup / Auto-Clean)
            if (request.FilePaths.Count > 0)
            {
                filePathsToClean = request.FilePaths.Distinct().ToList();
                _logger.LogInformation("🧹 Partial cleanup for session {AnalysisId}: {Count} files",
                    request.AnalysisId, filePathsToClean.Count);
            }
            else
            {
                // If no files specified, assume "Approve All" -> get all from session
                _logger.LogInformation("🔄 Using orchestrator to approve ALL items in analysis {AnalysisId}", request.AnalysisId);

                // Get the session's suggestions before approval
                var pendingAnalyses = await _orchestrator.GetPendingAnalysesAsync();
                var session = pendingAnalyses.FirstOrDefault(s => s.Id == request.AnalysisId);

                if (session == null)
                {
                    _logger.LogWarning("Session {AnalysisId} not found in pending analyses", request.AnalysisId);
                    return new CleanupResult
                    {
                        Success = false,
                        Errors = { $"Analysis session {request.AnalysisId} not found" }
                    };
                }

                // Get file paths from session - include ALL suggestions
                filePathsToClean = session.Suggestions
                    .Select(s => s.FilePath)
                    .Distinct()
                    .ToList();

                isFullSessionCleanup = true;
            }

            _logger.LogInformation("📁 Found {Count} paths to clean from session: {Paths}",
                filePathsToClean.Count,
                string.Join(", ", filePathsToClean));

            if (filePathsToClean.Count == 0)
            {
                return new CleanupResult
                {
                    Success = true,
                    FilesDeleted = 0,
                    BytesFreed = 0
                };
            }

            // Execute cleanup with the session's file paths
            var result = await _cleanupExecutor.ExecuteCleanupAsync(filePathsToClean, context.CancellationToken);

            // Update session state
            if (isFullSessionCleanup)
            {
                // Mark entire session as complete
                await _orchestrator.ApproveAnalysisAsync(request.AnalysisId, context.CancellationToken);
            }
            else if (result.Success)
            {
                // Mark only the cleaned items as complete
                await _orchestrator.CompleteItemsAsync(request.AnalysisId, filePathsToClean, context.CancellationToken);
            }

            return new CleanupResult
            {
                Success = result.Success,
                FilesDeleted = result.FilesDeleted,
                BytesFreed = result.BytesFreed,
                Errors = { result.Errors }
            };
        }

        // Fallback: use file paths from request directly (legacy behavior)
        if (request.FilePaths.Count == 0)
        {
            _logger.LogWarning("No file paths provided and no analysis ID");
            return new CleanupResult
            {
                Success = false,
                Errors = { "No files specified for cleanup" }
            };
        }

        var directResult = await _cleanupExecutor.ExecuteCleanupAsync(
            request.FilePaths,
            context.CancellationToken);

        return new CleanupResult
        {
            Success = directResult.Success,
            FilesDeleted = directResult.FilesDeleted,
            BytesFreed = directResult.BytesFreed,
            Errors = { directResult.Errors }
        };
    }

    public override Task<ServiceStatus> GetServiceStatus(
        StatusRequest request,
        ServerCallContext context)
    {
        return Task.FromResult(new ServiceStatus
        {
            IsRunning = true,
            IsMonitoring = true,
            UptimeSeconds = (long)TimeSpan.FromTicks(Environment.TickCount64).TotalSeconds,
            PendingAnalyses = 0
        });
    }

    public override Task<MonitoringActivityResponse> GetMonitoringActivity(
        MonitoringActivityRequest request,
        ServerCallContext context)
    {
        var since = request.SinceUnixTimeMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(request.SinceUnixTimeMs)
            : (DateTimeOffset?)null;

        var items = _activityStore.Snapshot(request.Limit, since)
            .Select(ToProto);

        var response = new MonitoringActivityResponse();
        response.Items.AddRange(items);
        return Task.FromResult(response);
    }

    private static MonitoringActivityItem ToProto(MonitoringActivity activity)
    {
        var item = new MonitoringActivityItem
        {
            Id = activity.Id,
            Type = activity.Type.ToString(),
            Scope = activity.Scope ?? string.Empty,
            Drive = activity.Drive ?? string.Empty,
            State = activity.State ?? string.Empty,
            Message = activity.Message ?? string.Empty,
            TimestampUnixTimeMs = activity.Timestamp.ToUnixTimeMilliseconds()
        };

        if (activity.Metadata != null)
        {
            foreach (var entry in activity.Metadata)
            {
                if (!string.IsNullOrWhiteSpace(entry.Key) && entry.Value != null)
                {
                    item.Metadata[entry.Key] = entry.Value;
                }
            }
        }

        return item;
    }
}
