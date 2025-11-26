using System.Diagnostics;
using Grpc.Core;
using SentinAI.Shared;
using SentinAI.Shared.Models;
using SentinAI.Web.Services;

namespace SentinAI.Web.Services;

/// <summary>
/// gRPC service that exposes the Brain (heuristic/AI) analysis to the Sentinel Service
/// This is the "Brain" endpoint that Sentinel calls for file analysis
/// </summary>
public class BrainGrpcService : AgentService.AgentServiceBase
{
    private readonly ILogger<BrainGrpcService> _logger;
    private readonly IAgentBrain _brain;
    private readonly IWinapp2Parser _winapp2Parser;
    private int _totalRequests;
    private int _successfulRequests;

    public BrainGrpcService(
        ILogger<BrainGrpcService> logger,
        IAgentBrain brain,
        IWinapp2Parser winapp2Parser)
    {
        _logger = logger;
        _brain = brain;
        _winapp2Parser = winapp2Parser;
    }

    public override async Task<CleanupSuggestions> GetCleanupSuggestions(
        CleanupRequest request,
        ServerCallContext context)
    {
        _totalRequests++;
        var sw = Stopwatch.StartNew();
        var requestId = Guid.NewGuid().ToString()[..8];

        _logger.LogInformation(
            "📥 [{RequestId}] Brain gRPC request received | Folder: {Folder} | Files: {FileCount}",
            requestId,
            request.FolderPath,
            request.FileNames.Count);

        _logger.LogDebug("📄 [{RequestId}] File list: {Files}",
            requestId,
            string.Join(", ", request.FileNames.Take(20)));

        try
        {
            // Check brain readiness
            if (!_brain.IsReady)
            {
                _logger.LogWarning("⚠️ [{RequestId}] Brain not ready - returning empty suggestions", requestId);
                return new CleanupSuggestions
                {
                    TotalBytesToFree = 0,
                    Reasoning = "Brain service not initialized"
                };
            }

            // Build file list for analysis
            var filePaths = request.FileNames
                .Select(name => Path.Combine(request.FolderPath, name))
                .ToList();

            if (!filePaths.Any())
            {
                _logger.LogWarning("⚠️ [{RequestId}] No files to analyze", requestId);
                return new CleanupSuggestions
                {
                    TotalBytesToFree = 0,
                    Reasoning = "No files provided for analysis"
                };
            }

            // Get file info for size calculations
            _logger.LogDebug("📊 [{RequestId}] Gathering file metadata...", requestId);
            var fileInfos = filePaths
                .Select(path =>
                {
                    try
                    {
                        var info = new FileInfo(path);
                        return (path, size: info.Length, lastAccess: info.LastAccessTime, exists: true);
                    }
                    catch
                    {
                        return (path, size: 0L, lastAccess: DateTime.MinValue, exists: false);
                    }
                })
                .ToList();

            var existingFiles = fileInfos.Count(f => f.exists);
            var totalSize = fileInfos.Sum(f => f.size);
            _logger.LogInformation("📊 [{RequestId}] File stats: {Existing}/{Total} exist, {Size:N0} bytes total",
                requestId, existingFiles, filePaths.Count, totalSize);

            // Run analysis through the Brain service
            _logger.LogInformation("🧠 [{RequestId}] Starting Brain analysis...", requestId);
            var analysisStart = sw.ElapsedMilliseconds;

            var sessionContext = string.IsNullOrWhiteSpace(request.SessionId)
                ? null
                : new BrainSessionContext(request.SessionId, request.QueryHint, null);

            var brainSuggestions = await _brain.AnalyzeFilesAsync(
                filePaths,
                sessionContext,
                context.CancellationToken);

            var analysisDuration = sw.ElapsedMilliseconds - analysisStart;
            _logger.LogInformation("🧠 [{RequestId}] Brain analysis completed in {Duration}ms | {Count} suggestions",
                requestId, analysisDuration, brainSuggestions?.Count ?? 0);

            if (brainSuggestions == null || brainSuggestions.Count == 0)
            {
                _logger.LogWarning("⚠️ [{RequestId}] Brain returned no suggestions", requestId);
                return new CleanupSuggestions
                {
                    TotalBytesToFree = 0,
                    Reasoning = "No cleanup opportunities detected"
                };
            }

            var suggestions = new CleanupSuggestions
            {
                Reasoning = brainSuggestions.FirstOrDefault()?.Reason ?? "Analysis complete"
            };

            foreach (var suggestion in brainSuggestions)
            {
                var path = string.IsNullOrWhiteSpace(suggestion.FilePath)
                    ? request.FolderPath
                    : suggestion.FilePath;

                var item = new CleanupItem
                {
                    FilePath = path,
                    SizeBytes = suggestion.SizeBytes,
                    Category = suggestion.Category,
                    SafeToDelete = suggestion.SafeToDelete,
                    Reason = suggestion.Reason,
                    AutoApprove = suggestion.AutoApprove,
                    Confidence = suggestion.Confidence  // Map AI confidence score
                };

                suggestions.Items.Add(item);
                suggestions.TotalBytesToFree += item.SizeBytes;
            }

            // Ground-truth check against Winapp2
            _logger.LogDebug("📋 [{RequestId}] Validating against Winapp2 rules...", requestId);
            var winapp2Overrides = 0;

            foreach (var suggestion in suggestions.Items)
            {
                var winapp2Safe = _winapp2Parser.IsSafeToDelete(suggestion.FilePath);
                if (winapp2Safe && !suggestion.SafeToDelete)
                {
                    _logger.LogInformation(
                        "✅ [{RequestId}] Winapp2 override: {File} marked SAFE",
                        requestId,
                        Path.GetFileName(suggestion.FilePath));
                    suggestion.SafeToDelete = true;
                    suggestion.Reason += " [Confirmed by Winapp2]";
                    winapp2Overrides++;
                }
                else if (!winapp2Safe && suggestion.SafeToDelete)
                {
                    _logger.LogWarning(
                        "🛑 [{RequestId}] Winapp2 override: {File} marked UNSAFE",
                        requestId,
                        Path.GetFileName(suggestion.FilePath));
                    suggestion.SafeToDelete = false;
                    suggestion.AutoApprove = false;
                    suggestion.Reason += " [Flagged by Winapp2]";
                    winapp2Overrides++;
                }
            }

            if (winapp2Overrides > 0)
            {
                _logger.LogInformation("📋 [{RequestId}] Winapp2 applied {Count} overrides", requestId, winapp2Overrides);
            }

            sw.Stop();
            _successfulRequests++;

            var safeCount = suggestions.Items.Count(i => i.SafeToDelete);
            var autoApproveCount = suggestions.Items.Count(i => i.AutoApprove);

            _logger.LogInformation(
                "📤 [{RequestId}] Response ready in {Duration}ms | " +
                "Safe: {SafeCount}/{Total} | AutoApprove: {AutoApprove} | Bytes: {Bytes:N0}",
                requestId,
                sw.ElapsedMilliseconds,
                safeCount,
                suggestions.Items.Count,
                autoApproveCount,
                suggestions.TotalBytesToFree);

            return suggestions;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "❌ [{RequestId}] Brain analysis failed after {Duration}ms",
                requestId, sw.ElapsedMilliseconds);

            return new CleanupSuggestions
            {
                TotalBytesToFree = 0,
                Reasoning = $"Analysis failed: {ex.Message}"
            };
        }
    }
}
