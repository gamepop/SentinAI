using System.Runtime.Versioning;
using Microsoft.AspNetCore.Mvc;
using SentinAI.Shared.Models.DeepScan;
using SentinAI.Web.Services.DeepScan;

namespace SentinAI.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[SupportedOSPlatform("windows")]
public class DeepScanController : ControllerBase
{
    private readonly DeepScanService _deepScanService;
    private readonly DeepScanExecutionService _executionService;
    private readonly DriveManagerService _driveManager;
    private readonly DeepScanLearningService _learningService;
    private readonly IDeepScanRagStore _ragStore;
    private readonly IDeepScanSessionStore _sessionStore;
    private readonly ILogger<DeepScanController> _logger;

    public DeepScanController(
        DeepScanService deepScanService,
        DeepScanExecutionService executionService,
        DriveManagerService driveManager,
        DeepScanLearningService learningService,
        IDeepScanRagStore ragStore,
        IDeepScanSessionStore sessionStore,
        ILogger<DeepScanController> logger)
    {
        _deepScanService = deepScanService;
        _executionService = executionService;
        _driveManager = driveManager;
        _learningService = learningService;
        _ragStore = ragStore;
        _sessionStore = sessionStore;
        _logger = logger;
    }

    /// <summary>
    /// Gets available drives for scanning.
    /// </summary>
    [HttpGet("drives")]
    public async Task<ActionResult<List<TargetDriveInfo>>> GetDrives()
    {
        try
        {
            var drives = await _driveManager.GetAvailableDrivesAsync();
            return Ok(drives);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get available drives");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Starts a new deep scan session.
    /// </summary>
    [HttpPost("start")]
    public async Task<ActionResult<DeepScanSession>> StartScan([FromBody] DeepScanOptions options)
    {
        try
        {
            if (options.TargetDrives == null || options.TargetDrives.Count == 0)
            {
                return BadRequest(new { error = "No target drives specified" });
            }

            _logger.LogInformation("Starting deep scan for drives: {Drives}", string.Join(", ", options.TargetDrives));
            var session = await _deepScanService.StartScanAsync(options, HttpContext.RequestAborted);
            return Ok(session);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start deep scan");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Gets a scan session by ID.
    /// </summary>
    [HttpGet("{sessionId:guid}")]
    public async Task<ActionResult<DeepScanSession>> GetSession(Guid sessionId)
    {
        var session = await _deepScanService.GetSessionAsync(sessionId);
        if (session == null)
        {
            return NotFound(new { error = "Session not found" });
        }
        return Ok(session);
    }

    /// <summary>
    /// Gets the most recent scan session (for restoring state after navigation).
    /// </summary>
    [HttpGet("latest")]
    public async Task<ActionResult<DeepScanSession>> GetLatestSession()
    {
        var session = await _deepScanService.GetLatestSessionAsync();
        if (session == null)
        {
            return NotFound(new { error = "No sessions found" });
        }
        return Ok(session);
    }

    /// <summary>
    /// Gets session history.
    /// </summary>
    [HttpGet("history")]
    public async Task<ActionResult<List<DeepScanSessionSummary>>> GetSessionHistory([FromQuery] int limit = 10)
    {
        var history = await _deepScanService.GetSessionHistoryAsync(limit);
        return Ok(history);
    }

    /// <summary>
    /// Cancels an ongoing scan.
    /// </summary>
    [HttpPost("{sessionId:guid}/cancel")]
    public async Task<ActionResult> CancelScan(Guid sessionId)
    {
        var session = await _deepScanService.GetSessionAsync(sessionId);
        if (session == null)
        {
            return NotFound(new { error = "Session not found" });
        }

        _deepScanService.CancelScan(sessionId);
        return Ok(new { message = "Scan cancelled" });
    }

    /// <summary>
    /// Records feedback for app removal recommendation.
    /// </summary>
    [HttpPost("feedback/app")]
    public async Task<ActionResult> RecordAppFeedback([FromBody] AppFeedbackRequest request)
    {
        try
        {
            if (request.Recommendation == null)
            {
                return BadRequest(new { error = "Recommendation is required" });
            }

            await _deepScanService.RecordAppFeedbackAsync(request.Recommendation, request.UserApproved);
            return Ok(new { message = "Feedback recorded" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record app feedback");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Records feedback for relocation recommendation.
    /// </summary>
    [HttpPost("feedback/relocation")]
    public async Task<ActionResult> RecordRelocationFeedback([FromBody] RelocationFeedbackRequest request)
    {
        try
        {
            if (request.Recommendation == null)
            {
                return BadRequest(new { error = "Recommendation is required" });
            }

            await _deepScanService.RecordRelocationFeedbackAsync(
                request.Recommendation,
                request.UserApproved,
                request.ActualTargetDrive);
            return Ok(new { message = "Feedback recorded" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record relocation feedback");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Records feedback for cleanup opportunity.
    /// </summary>
    [HttpPost("feedback/cleanup")]
    public async Task<ActionResult> RecordCleanupFeedback([FromBody] CleanupFeedbackRequest request)
    {
        try
        {
            if (request.Opportunity == null)
            {
                return BadRequest(new { error = "Opportunity is required" });
            }

            await _deepScanService.RecordCleanupFeedbackAsync(request.Opportunity, request.UserApproved);
            return Ok(new { message = "Feedback recorded" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record cleanup feedback");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Gets learning statistics.
    /// </summary>
    [HttpGet("learning/stats")]
    public async Task<ActionResult<DeepScanLearningStats>> GetLearningStats()
    {
        try
        {
            var stats = await _ragStore.GetLearningStatsAsync();
            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get learning stats");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // ========== Execution Endpoints ==========

    /// <summary>
    /// Executes all approved cleanup operations for a session.
    /// </summary>
    [HttpPost("{sessionId:guid}/execute/cleanup")]
    public async Task<ActionResult<ExecutionResult>> ExecuteCleanup(Guid sessionId)
    {
        try
        {
            var session = await _deepScanService.GetSessionAsync(sessionId);
            if (session == null)
            {
                return NotFound(new { error = "Session not found" });
            }

            var result = await _executionService.ExecuteCleanupAsync(session, null, HttpContext.RequestAborted);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute cleanup for session {SessionId}", sessionId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Executes all approved app uninstallations for a session.
    /// </summary>
    [HttpPost("{sessionId:guid}/execute/apps")]
    public async Task<ActionResult<ExecutionResult>> ExecuteAppUninstalls(Guid sessionId)
    {
        try
        {
            var session = await _deepScanService.GetSessionAsync(sessionId);
            if (session == null)
            {
                return NotFound(new { error = "Session not found" });
            }

            var result = await _executionService.ExecuteAppRemovalAsync(session, null, HttpContext.RequestAborted);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute app uninstalls for session {SessionId}", sessionId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Executes all approved file relocations for a session.
    /// </summary>
    [HttpPost("{sessionId:guid}/execute/relocations")]
    public async Task<ActionResult<ExecutionResult>> ExecuteRelocations(Guid sessionId)
    {
        try
        {
            var session = await _deepScanService.GetSessionAsync(sessionId);
            if (session == null)
            {
                return NotFound(new { error = "Session not found" });
            }

            var result = await _executionService.ExecuteRelocationAsync(session, null, HttpContext.RequestAborted);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute relocations for session {SessionId}", sessionId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Executes all approved duplicate removals for a session.
    /// </summary>
    [HttpPost("{sessionId:guid}/execute/duplicates")]
    public async Task<ActionResult<ExecutionResult>> ExecuteDuplicateRemoval(Guid sessionId)
    {
        try
        {
            var session = await _deepScanService.GetSessionAsync(sessionId);
            if (session == null)
            {
                return NotFound(new { error = "Session not found" });
            }

            var result = await _executionService.ExecuteDuplicateRemovalAsync(session, null, HttpContext.RequestAborted);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute duplicate removal for session {SessionId}", sessionId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Executes all approved actions for a session (cleanup, apps, relocations, duplicates).
    /// </summary>
    [HttpPost("{sessionId:guid}/execute/all")]
    public async Task<ActionResult<ExecutionResult>> ExecuteAll(Guid sessionId)
    {
        try
        {
            var session = await _deepScanService.GetSessionAsync(sessionId);
            if (session == null)
            {
                return NotFound(new { error = "Session not found" });
            }

            var result = await _executionService.ExecuteAllApprovedAsync(session, null, HttpContext.RequestAborted);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute all actions for session {SessionId}", sessionId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Batch approves all "safe" recommendations (high confidence, low risk).
    /// </summary>
    [HttpPost("{sessionId:guid}/approve-safe")]
    public async Task<ActionResult<BatchApprovalResult>> ApproveAllSafe(Guid sessionId, [FromQuery] double minConfidence = 0.8)
    {
        try
        {
            var session = await _deepScanService.GetSessionAsync(sessionId);
            if (session == null)
            {
                return NotFound(new { error = "Session not found" });
            }

            var approvedCount = await _executionService.ApproveAllSafeAsync(session, minConfidence);
            return Ok(new BatchApprovalResult
            {
                ApprovedCount = approvedCount,
                Message = $"Approved {approvedCount} safe recommendations"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to approve safe recommendations for session {SessionId}", sessionId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Approves or rejects an app removal recommendation.
    /// </summary>
    [HttpPost("{sessionId:guid}/app/{appId}/status")]
    public async Task<ActionResult> UpdateAppStatus(Guid sessionId, string appId, [FromBody] UpdateStatusRequest request)
    {
        try
        {
            var session = await _deepScanService.GetSessionAsync(sessionId);
            if (session == null)
            {
                return NotFound(new { error = "Session not found" });
            }

            var recommendation = session.AppRemovalRecommendations?.FirstOrDefault(r => r.App?.Id == appId);
            if (recommendation == null)
            {
                return NotFound(new { error = "Recommendation not found" });
            }

            recommendation.Status = request.Approved ? RecommendationStatus.Approved : RecommendationStatus.Rejected;

            // Save the session to persist the status change
            await _sessionStore.SaveSessionAsync(session);

            // Record learning
            await _deepScanService.RecordAppFeedbackAsync(recommendation, request.Approved);

            return Ok(new { message = "Status updated" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update app status for session {SessionId}", sessionId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Approves or rejects a relocation recommendation.
    /// </summary>
    [HttpPost("{sessionId:guid}/relocation/{clusterId}/status")]
    public async Task<ActionResult> UpdateRelocationStatus(Guid sessionId, string clusterId, [FromBody] UpdateStatusRequest request)
    {
        try
        {
            var session = await _deepScanService.GetSessionAsync(sessionId);
            if (session == null)
            {
                return NotFound(new { error = "Session not found" });
            }

            var recommendation = session.RelocationRecommendations?.FirstOrDefault(r => r.Cluster?.Id == clusterId);
            if (recommendation == null)
            {
                return NotFound(new { error = "Recommendation not found" });
            }

            recommendation.Status = request.Approved ? RecommendationStatus.Approved : RecommendationStatus.Rejected;

            // Save the session to persist the status change
            await _sessionStore.SaveSessionAsync(session);

            // Record learning
            await _deepScanService.RecordRelocationFeedbackAsync(recommendation, request.Approved, recommendation.TargetDrive);

            return Ok(new { message = "Status updated" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update relocation status for session {SessionId}", sessionId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Approves or rejects a cleanup opportunity.
    /// </summary>
    [HttpPost("{sessionId:guid}/cleanup/{cleanupId}/status")]
    public async Task<ActionResult> UpdateCleanupStatus(Guid sessionId, string cleanupId, [FromBody] UpdateStatusRequest request)
    {
        try
        {
            var session = await _deepScanService.GetSessionAsync(sessionId);
            if (session == null)
            {
                return NotFound(new { error = "Session not found" });
            }

            var opportunity = session.CleanupOpportunities?.FirstOrDefault(o => o.Id == cleanupId);
            if (opportunity == null)
            {
                return NotFound(new { error = "Opportunity not found" });
            }

            opportunity.Status = request.Approved ? RecommendationStatus.Approved : RecommendationStatus.Rejected;

            // Save the session to persist the status change
            await _sessionStore.SaveSessionAsync(session);

            // Record learning
            await _deepScanService.RecordCleanupFeedbackAsync(opportunity, request.Approved);

            return Ok(new { message = "Status updated" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update cleanup status for session {SessionId}", sessionId);
            return StatusCode(500, new { error = ex.Message });
        }
    }
}

// Request DTOs
public class UpdateStatusRequest
{
    public bool Approved { get; set; }
}

public class AppFeedbackRequest
{
    public AppRemovalRecommendation? Recommendation { get; set; }
    public bool UserApproved { get; set; }
}

public class RelocationFeedbackRequest
{
    public RelocationRecommendation? Recommendation { get; set; }
    public bool UserApproved { get; set; }
    public string? ActualTargetDrive { get; set; }
}

public class CleanupFeedbackRequest
{
    public CleanupOpportunity? Opportunity { get; set; }
    public bool UserApproved { get; set; }
}

// Response DTOs
public class BatchApprovalResult
{
    public int ApprovedCount { get; set; }
    public string Message { get; set; } = string.Empty;
}
