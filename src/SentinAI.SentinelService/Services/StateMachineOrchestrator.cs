using System.Collections.Generic;
using System.IO;
using System.Linq;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using SentinAI.Shared;
using SentinAI.Shared.Models;
using SentinAI.Shared.Services;

namespace SentinAI.SentinelService.Services;

/// <summary>
/// Orchestrates the state machine: IDLE → TRIAGE → PROPOSAL → APPROVAL → EXECUTION → REPORT
/// </summary>
public interface IStateMachineOrchestrator
{
    Task ProcessEventsAsync(List<UsnJournalEntry> events, CancellationToken cancellationToken);
    Task<List<AnalysisSession>> GetPendingAnalysesAsync();
    Task ApproveAnalysisAsync(string analysisId, CancellationToken cancellationToken);
    Task RejectAnalysisAsync(string analysisId);

    /// <summary>
    /// Trigger on-demand analysis of specific folders
    /// </summary>
    Task<(bool Accepted, string AnalysisId, int FoldersQueued)> AnalyzeNowAsync(
        List<string> folderPaths,
        string reason,
        CancellationToken cancellationToken);
}

public class StateMachineOrchestrator : IStateMachineOrchestrator
{
    private readonly ILogger<StateMachineOrchestrator> _logger;
    private readonly ICleanupExecutor _cleanupExecutor;
    private readonly AgentService.AgentServiceClient? _brainClient;
    private readonly IMonitoringActivityPublisher _activityPublisher;
    private readonly Dictionary<string, AnalysisSession> _sessions = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public StateMachineOrchestrator(
        ILogger<StateMachineOrchestrator> logger,
        ICleanupExecutor cleanupExecutor,
        AgentService.AgentServiceClient? brainClient = null,
        IMonitoringActivityPublisher? activityPublisher = null)
    {
        _logger = logger;
        _cleanupExecutor = cleanupExecutor;
        _brainClient = brainClient;
        _activityPublisher = activityPublisher ?? new NoopMonitoringActivityPublisher();
    }

    public async Task ProcessEventsAsync(List<UsnJournalEntry> events, CancellationToken cancellationToken)
    {
        AnalysisSession? session;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            session = await TriageAsync(events);

            if (session == null)
            {
                _logger.LogInformation("Triage determined no action needed");
                return;
            }

            _sessions[session.Id] = session;
        }
        finally
        {
            _lock.Release();
        }

        await PublishActivityAsync(session, "Triage", new Dictionary<string, string>
        {
            ["eventCount"] = events.Count.ToString()
        });

        await GenerateProposalAsync(session, cancellationToken);
    }

    private async Task GenerateProposalAsync(AnalysisSession session, CancellationToken cancellationToken)
    {
        List<CleanupSuggestion> suggestions;

        try
        {
            suggestions = await AnalyzeWithBrainAsync(session, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Brain analysis failed for session {SessionId}", session.Id);
            suggestions = new List<CleanupSuggestion>();
        }

        bool autoApprove = false;
        bool noSuggestions = false;
        int suggestionCount = 0;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!_sessions.ContainsKey(session.Id))
            {
                return;
            }

            if (!suggestions.Any())
            {
                _sessions.Remove(session.Id);
                _logger.LogInformation("No cleanup suggestions generated for session {SessionId}", session.Id);
                noSuggestions = true;
            }

            session.Suggestions = suggestions;
            session.CurrentState = AgentState.Approval;
            session.RequiresUserApproval = suggestions.Any(s => !s.AutoApprove);
            autoApprove = !session.RequiresUserApproval;
            suggestionCount = suggestions.Count;

            if (autoApprove)
            {
                _logger.LogInformation("Session {SessionId} auto-approved (all suggestions safe)", session.Id);
            }
            else
            {
                _logger.LogInformation(
                    "Session {SessionId} awaiting user approval with {Count} suggestion(s)",
                    session.Id,
                    session.Suggestions.Count);
            }
        }
        finally
        {
            _lock.Release();
        }

        if (noSuggestions)
        {
            await PublishActivityAsync(session, "ProposalEmpty");
            return;
        }

        await PublishActivityAsync(session, autoApprove ? "AutoApproved" : "AwaitingApproval", new Dictionary<string, string>
        {
            ["suggestions"] = suggestionCount.ToString(),
            ["autoApproved"] = autoApprove.ToString()
        });

        if (autoApprove)
        {
            await ApproveAnalysisAsync(session.Id, cancellationToken);
        }
    }

    private async Task<AnalysisSession?> TriageAsync(List<UsnJournalEntry> events)
    {
        _logger.LogInformation("STATE: TRIAGE - Analyzing {Count} events", events.Count);

        var fileEvents = events.Select(e => new FileEvent
        {
            FilePath = e.FullPath,
            SizeBytes = e.FileSize,
            Timestamp = e.Timestamp.Ticks,
            Reason = e.Reason.ToString()
        }).ToList();

        bool hasNodeModules = events.Any(e =>
            e.FullPath.Contains("node_modules", StringComparison.OrdinalIgnoreCase));
        bool hasTempFiles = events.Any(e =>
            e.FullPath.Contains("temp", StringComparison.OrdinalIgnoreCase));
        bool hasHeavyWrites = events.Any(e => e.FileSize > 500 * 1024 * 1024);

        if (!hasNodeModules && !hasTempFiles && !hasHeavyWrites)
        {
            return null;
        }

        var session = new AnalysisSession
        {
            CurrentState = AgentState.Triage,
            TriggerEvents = fileEvents
        };

        _logger.LogInformation(
            "Triage detected: NodeModules={NodeModules}, Temp={Temp}, HeavyWrites={Heavy}",
            hasNodeModules, hasTempFiles, hasHeavyWrites);

        session.CurrentState = AgentState.Proposal;

        return await Task.FromResult(session);
    }

    public async Task<List<AnalysisSession>> GetPendingAnalysesAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return _sessions.Values
                .Where(s => s.CurrentState == AgentState.Approval && s.RequiresUserApproval)
                .Select(s => s)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ApproveAnalysisAsync(string analysisId, CancellationToken cancellationToken)
    {
        AnalysisSession? session;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!_sessions.TryGetValue(analysisId, out session))
            {
                _logger.LogWarning("Analysis session {SessionId} not found", analysisId);
                return;
            }

            _logger.LogInformation("STATE: APPROVAL → EXECUTION for session {SessionId}", analysisId);

            session.CurrentState = AgentState.Execution;
            session.UserApproved = true;
        }
        finally
        {
            _lock.Release();
        }

        if (session == null)
        {
            return;
        }

        await PublishActivityAsync(session, "Execution", new Dictionary<string, string>
        {
            ["approved"] = session.UserApproved.ToString()
        });

        await ExecuteSessionCleanupAsync(session, cancellationToken);
    }

    public async Task RejectAnalysisAsync(string analysisId)
    {
        AnalysisSession? session = null;
        await _lock.WaitAsync();
        try
        {
            if (_sessions.TryGetValue(analysisId, out session))
            {
                _logger.LogInformation("Analysis {SessionId} rejected by user", analysisId);
                session.CurrentState = AgentState.Idle;
                session.CompletedAt = DateTime.UtcNow;
                _sessions.Remove(analysisId);
            }
        }
        finally
        {
            _lock.Release();
        }

        if (session != null)
        {
            await PublishActivityAsync(session, "Rejected");
        }
    }

    /// <summary>
    /// Trigger on-demand analysis of specific folders
    /// </summary>
    public async Task<(bool Accepted, string AnalysisId, int FoldersQueued)> AnalyzeNowAsync(
        List<string> folderPaths,
        string reason,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("🔍 Analyze Now triggered: {Count} folders, Reason: {Reason}",
            folderPaths.Count, reason);

        await _activityPublisher.PublishAsync(new MonitoringActivity
        {
            Type = MonitoringActivityType.DriveSweep,
            Scope = "ManualAnalysis",
            State = "Triggered",
            Message = $"Manual analysis triggered for {folderPaths.Count} folder(s)",
            Metadata = new Dictionary<string, string>
            {
                ["reason"] = reason,
                ["folderCount"] = folderPaths.Count.ToString(),
                ["folders"] = string.Join(", ", folderPaths.Take(5))
            }
        }, cancellationToken);

        // Build synthetic file events for the folders
        var fileEvents = new List<FileEvent>();
        var foldersProcessed = 0;

        foreach (var folderPath in folderPaths)
        {
            if (!Directory.Exists(folderPath))
            {
                _logger.LogWarning("⚠️ Folder does not exist: {Folder}", folderPath);
                continue;
            }

            try
            {
                // Get files in the folder (non-recursive for performance)
                var files = Directory.GetFiles(folderPath, "*", SearchOption.TopDirectoryOnly);

                _logger.LogInformation("📂 Scanning folder: {Folder} ({Count} files)", folderPath, files.Length);

                foreach (var file in files.Take(100)) // Limit to 100 files per folder
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        fileEvents.Add(new FileEvent
                        {
                            FilePath = file,
                            SizeBytes = fi.Length,
                            Timestamp = fi.LastWriteTimeUtc.Ticks,
                            Reason = "ManualScan"
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not get info for file: {File}", file);
                    }
                }

                foldersProcessed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Error scanning folder: {Folder}", folderPath);
            }
        }

        if (fileEvents.Count == 0)
        {
            _logger.LogWarning("⚠️ No files found in specified folders");
            return (false, string.Empty, 0);
        }

        _logger.LogInformation("📊 Found {Count} files across {Folders} folders", fileEvents.Count, foldersProcessed);

        // Create an analysis session
        var session = new AnalysisSession
        {
            Id = Guid.NewGuid().ToString(),
            CurrentState = AgentState.Triage,
            TriggerEvents = fileEvents,
            Scope = $"ManualAnalysis: {reason}"
        };

        await _lock.WaitAsync(cancellationToken);
        try
        {
            _sessions[session.Id] = session;
        }
        finally
        {
            _lock.Release();
        }

        await PublishActivityAsync(session, "ManualTriage", new Dictionary<string, string>
        {
            ["fileCount"] = fileEvents.Count.ToString(),
            ["folderCount"] = foldersProcessed.ToString()
        });

        // Process in background
        _ = Task.Run(async () =>
        {
            try
            {
                await GenerateProposalAsync(session, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in background analysis for session {SessionId}", session.Id);
            }
        }, cancellationToken);

        return (true, session.Id, foldersProcessed);
    }

    private async Task ExecuteSessionCleanupAsync(AnalysisSession session, CancellationToken cancellationToken)
    {
        var filePaths = session.Suggestions
            .Where(s => s.SafeToDelete)
            .Select(s => s.FilePath)
            .Distinct()
            .ToList();

        var result = await _cleanupExecutor.ExecuteCleanupAsync(filePaths, cancellationToken);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            session.CurrentState = AgentState.Report;
            session.CompletedAt = DateTime.UtcNow;

            _logger.LogInformation(
                "STATE: REPORT - Cleanup completed. Files deleted: {Count}, Bytes freed: {Bytes}",
                result.FilesDeleted,
                result.BytesFreed);

            session.CurrentState = AgentState.Idle;
            _sessions.Remove(session.Id);
        }
        finally
        {
            _lock.Release();
        }

        await PublishActivityAsync(session, "Report", new Dictionary<string, string>
        {
            ["filesDeleted"] = result.FilesDeleted.ToString(),
            ["bytesFreed"] = result.BytesFreed.ToString()
        });
    }

    private async Task<List<CleanupSuggestion>> AnalyzeWithBrainAsync(
        AnalysisSession session,
        CancellationToken cancellationToken)
    {
        var events = session.TriggerEvents;
        if (_brainClient == null)
        {
            _logger.LogWarning("\u26a0\ufe0f Brain client not configured; using heuristic fallback");
            await _activityPublisher.PublishAsync(new MonitoringActivity
            {
                Type = MonitoringActivityType.BrainConnection,
                Scope = "Brain",
                State = "Disconnected",
                Message = "Brain client not configured, using fallback heuristics"
            });
            return GenerateFallbackSuggestions(events);
        }

        _logger.LogInformation("\ud83e\udde0 Connecting to Brain for AI analysis of {Count} events", events.Count);
        await _activityPublisher.PublishAsync(new MonitoringActivity
        {
            Type = MonitoringActivityType.BrainConnection,
            Scope = "Brain",
            State = "Connecting",
            Message = $"Initiating AI analysis for {events.Count} file events"
        });

        var suggestions = new List<CleanupSuggestion>();

        var groups = events
            .Where(e => !string.IsNullOrWhiteSpace(e.FilePath))
            .GroupBy(e => Path.GetDirectoryName(e.FilePath) ?? string.Empty);

        var groupCount = 0;
        var totalGroups = groups.Count();

        foreach (var group in groups)
        {
            groupCount++;
            var folder = string.IsNullOrWhiteSpace(group.Key)
                ? Path.GetDirectoryName(group.First().FilePath) ?? "C:\\"
                : group.Key;

            var request = new CleanupRequest
            {
                FolderPath = folder,
                SessionId = session.Id,
                QueryHint = session.Scope ?? string.Empty
            };

            foreach (var fileEvent in group)
            {
                var fileName = Path.GetFileName(fileEvent.FilePath);
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    request.FileNames.Add(fileName);
                }
            }

            if (request.FileNames.Count == 0)
            {
                continue;
            }

            try
            {
                _logger.LogInformation("\ud83d\udce4 [{Current}/{Total}] Sending to Brain: {Folder} ({FileCount} files)",
                    groupCount, totalGroups, folder, request.FileNames.Count);

                await _activityPublisher.PublishAsync(new MonitoringActivity
                {
                    Type = MonitoringActivityType.ModelInteraction,
                    Scope = "Brain",
                    State = "Request",
                    Message = $"Analyzing folder: {folder}",
                    Metadata = new Dictionary<string, string>
                    {
                        ["folder"] = folder,
                        ["fileCount"] = request.FileNames.Count.ToString(),
                        ["progress"] = $"{groupCount}/{totalGroups}"
                    }
                });

                var response = await _brainClient.GetCleanupSuggestionsAsync(request, cancellationToken: cancellationToken);

                _logger.LogInformation("\ud83d\udce5 Brain returned {Count} suggestions for {Folder}",
                    response.Items.Count, folder);

                await _activityPublisher.PublishAsync(new MonitoringActivity
                {
                    Type = MonitoringActivityType.ModelInteraction,
                    Scope = "Brain",
                    State = "Response",
                    Message = $"Received {response.Items.Count} suggestions for {folder}",
                    Metadata = new Dictionary<string, string>
                    {
                        ["folder"] = folder,
                        ["suggestionsCount"] = response.Items.Count.ToString(),
                        ["totalBytes"] = response.TotalBytesToFree.ToString(),
                        ["reasoning"] = response.Reasoning ?? ""
                    }
                });

                foreach (var item in response.Items)
                {
                    _logger.LogDebug("  \u2022 {File}: SafeToDelete={Safe}, Category={Category}, Reason={Reason}",
                        item.FilePath, item.SafeToDelete, item.Category, item.Reason);

                    suggestions.Add(new CleanupSuggestion
                    {
                        FilePath = item.FilePath,
                        SizeBytes = item.SizeBytes,
                        Category = item.Category,
                        SafeToDelete = item.SafeToDelete,
                        Reason = item.Reason,
                        AutoApprove = item.AutoApprove,
                        Confidence = 0.0
                    });
                }
            }
            catch (RpcException rpcEx)
            {
                _logger.LogError(rpcEx, "\u274c Brain gRPC call failed for folder {Folder}: {Status}", folder, rpcEx.StatusCode);
                await _activityPublisher.PublishAsync(new MonitoringActivity
                {
                    Type = MonitoringActivityType.BrainConnection,
                    Scope = "Brain",
                    State = "Error",
                    Message = $"gRPC error: {rpcEx.StatusCode} - {rpcEx.Status.Detail}",
                    Metadata = new Dictionary<string, string>
                    {
                        ["folder"] = folder,
                        ["statusCode"] = rpcEx.StatusCode.ToString(),
                        ["detail"] = rpcEx.Status.Detail ?? ""
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "\u274c Unexpected error calling Brain for folder {Folder}", folder);
                await _activityPublisher.PublishAsync(new MonitoringActivity
                {
                    Type = MonitoringActivityType.BrainConnection,
                    Scope = "Brain",
                    State = "Error",
                    Message = $"Unexpected error: {ex.Message}",
                    Metadata = new Dictionary<string, string>
                    {
                        ["folder"] = folder,
                        ["exception"] = ex.GetType().Name,
                        ["message"] = ex.Message
                    }
                });
            }
        }

        if (!suggestions.Any())
        {
            _logger.LogWarning("\u26a0\ufe0f Brain returned no suggestions; using heuristic fallback");
            await _activityPublisher.PublishAsync(new MonitoringActivity
            {
                Type = MonitoringActivityType.BrainConnection,
                Scope = "Brain",
                State = "Fallback",
                Message = "No suggestions from Brain, using heuristic fallback"
            });
            return GenerateFallbackSuggestions(events);
        }

        _logger.LogInformation("\u2705 Brain analysis complete: {Count} total suggestions", suggestions.Count);
        await _activityPublisher.PublishAsync(new MonitoringActivity
        {
            Type = MonitoringActivityType.BrainConnection,
            Scope = "Brain",
            State = "Complete",
            Message = $"Analysis complete with {suggestions.Count} suggestions",
            Metadata = new Dictionary<string, string>
            {
                ["totalSuggestions"] = suggestions.Count.ToString(),
                ["safeToDelete"] = suggestions.Count(s => s.SafeToDelete).ToString(),
                ["autoApprove"] = suggestions.Count(s => s.AutoApprove).ToString()
            }
        });

        return suggestions;
    }

    private List<CleanupSuggestion> GenerateFallbackSuggestions(List<FileEvent> events)
    {
        var suggestions = new List<CleanupSuggestion>();

        foreach (var fileEvent in events)
        {
            if (string.IsNullOrWhiteSpace(fileEvent.FilePath))
            {
                continue;
            }

            var path = fileEvent.FilePath;
            var fileName = Path.GetFileName(path).ToLowerInvariant();
            var directory = Path.GetDirectoryName(path)?.ToLowerInvariant() ?? string.Empty;

            string category = CleanupCategories.Unknown;
            bool safe = false;
            bool autoApprove = false;
            string reason = "Unknown file type";

            if (fileName.EndsWith(".tmp") || fileName.EndsWith(".temp"))
            {
                category = CleanupCategories.Temp;
                safe = true;
                autoApprove = true;
                reason = "Temporary file";
            }
            else if (fileName.EndsWith(".log"))
            {
                category = CleanupCategories.Logs;
                safe = true;
                autoApprove = true;
                reason = "Log file";
            }
            else if (directory.Contains("cache"))
            {
                category = CleanupCategories.Cache;
                safe = true;
                autoApprove = true;
                reason = "Cache directory";
            }
            else if (directory.Contains("node_modules"))
            {
                category = CleanupCategories.NodeModules;
                safe = true;
                autoApprove = false;
                reason = "node_modules directory";
            }
            else if (directory.Contains("temp"))
            {
                category = CleanupCategories.Temp;
                safe = true;
                autoApprove = true;
                reason = "Temporary directory";
            }

            if (!safe)
            {
                continue;
            }

            suggestions.Add(new CleanupSuggestion
            {
                FilePath = path,
                SizeBytes = fileEvent.SizeBytes,
                Category = category,
                SafeToDelete = true,
                Reason = reason,
                AutoApprove = autoApprove
            });
        }

        return suggestions;
    }

    private Task PublishActivityAsync(AnalysisSession session, string state, IReadOnlyDictionary<string, string>? metadata = null)
    {
        return _activityPublisher.PublishAsync(new MonitoringActivity
        {
            Type = MonitoringActivityType.StateTransition,
            Scope = string.IsNullOrWhiteSpace(session.Scope) ? "Unknown" : session.Scope,
            State = state,
            Message = $"Session {session.Id} transitioned to {state}",
            Metadata = metadata
        });
    }

    private sealed class NoopMonitoringActivityPublisher : IMonitoringActivityPublisher
    {
        public Task PublishAsync(MonitoringActivity activity, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
