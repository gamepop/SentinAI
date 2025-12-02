using Microsoft.AspNetCore.Mvc;
using SentinAI.Shared.Models.DeepScan;
using SentinAI.Web.Services.DeepScan;

namespace SentinAI.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DeepScanController : ControllerBase
{
    private readonly ILogger<DeepScanController> _logger;
    private readonly DeepScanService _deepScanService;
    private readonly DriveManagerService _driveManager;
    private readonly AppDiscoveryService _appDiscovery;
    private readonly DeepScanLearningService _learningService;
    private readonly IDeepScanRagStore _ragStore;
    
    public DeepScanController(
        ILogger<DeepScanController> logger,
        DeepScanService deepScanService,
        DriveManagerService driveManager,
        AppDiscoveryService appDiscovery,
        DeepScanLearningService learningService,
        IDeepScanRagStore ragStore)
    {
        _logger = logger;
        _deepScanService = deepScanService;
        _driveManager = driveManager;
        _appDiscovery = appDiscovery;
        _learningService = learningService;
        _ragStore = ragStore;
    }
    
    /// <summary>
    /// Starts a new deep scan session.
    /// </summary>
    [HttpPost("start")]
    public async Task<ActionResult<DeepScanSession>> StartScan([FromBody] DeepScanOptions? options = null)
    {
        try
        {
            var session = await _deepScanService.StartDeepScanAsync(options);
            return Ok(session);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start deep scan");
            return StatusCode(500, new { error = "Failed to start deep scan", message = ex.Message });
        }
    }
    
    /// <summary>
    /// Gets the progress of a scan session.
    /// </summary>
    [HttpGet("{sessionId}/progress")]
    public ActionResult<DeepScanProgress> GetProgress(string sessionId)
    {
        var progress = _deepScanService.GetProgress(sessionId);
        if (progress == null)
            return NotFound(new { error = "Session not found" });
        
        return Ok(progress);
    }
    
    /// <summary>
    /// Gets the full session including results.
    /// </summary>
    [HttpGet("{sessionId}")]
    public ActionResult<DeepScanSession> GetSession(string sessionId)
    {
        var session = _deepScanService.GetSession(sessionId);
        if (session == null)
            return NotFound(new { error = "Session not found" });
        
        return Ok(session);
    }
    
    /// <summary>
    /// Cancels a running scan.
    /// </summary>
    [HttpPost("{sessionId}/cancel")]
    public async Task<ActionResult> CancelScan(string sessionId)
    {
        await _deepScanService.CancelScanAsync(sessionId);
        return Ok(new { message = "Cancellation requested" });
    }
    
    /// <summary>
    /// Gets all available drives with space information.
    /// </summary>
    [HttpGet("drives")]
    public ActionResult<List<TargetDriveInfo>> GetDrives()
    {
        var drives = _driveManager.GetAvailableDrives();
        return Ok(drives);
    }
    
    /// <summary>
    /// Gets installed apps (quick discovery without full scan).
    /// </summary>
    [HttpGet("apps")]
    public async Task<ActionResult<List<InstalledApp>>> GetInstalledApps(CancellationToken ct)
    {
        var apps = new List<InstalledApp>();
        
        await foreach (var app in _appDiscovery.DiscoverAppsAsync(ct))
        {
            apps.Add(app);
        }
        
        return Ok(apps.OrderByDescending(a => a.TotalSizeBytes).ToList());
    }
    
    /// <summary>
    /// Submits user feedback on a recommendation for learning.
    /// </summary>
    [HttpPost("feedback/app")]
    public async Task<ActionResult> SubmitAppFeedback([FromBody] AppFeedbackRequest request)
    {
        await _learningService.RecordAppDecisionAsync(
            request.Recommendation,
            request.UserApproved,
            request.UserReason);
        
        return Ok(new { message = "Feedback recorded for learning" });
    }
    
    /// <summary>
    /// Submits user feedback on a relocation recommendation.
    /// </summary>
    [HttpPost("feedback/relocation")]
    public async Task<ActionResult> SubmitRelocationFeedback([FromBody] RelocationFeedbackRequest request)
    {
        await _learningService.RecordRelocationDecisionAsync(
            request.Recommendation,
            request.UserApproved,
            request.ActualTargetDrive);
        
        return Ok(new { message = "Feedback recorded for learning" });
    }
    
    /// <summary>
    /// Submits user feedback on a cleanup opportunity.
    /// </summary>
    [HttpPost("feedback/cleanup")]
    public async Task<ActionResult> SubmitCleanupFeedback([FromBody] CleanupFeedbackRequest request)
    {
        await _learningService.RecordCleanupDecisionAsync(
            request.Opportunity,
            request.UserApproved);
        
        return Ok(new { message = "Feedback recorded for learning" });
    }
    
    /// <summary>
    /// Gets learning statistics.
    /// </summary>
    [HttpGet("learning/stats")]
    public async Task<ActionResult<DeepScanLearningStats>> GetLearningStats()
    {
        var stats = await _ragStore.GetLearningStatsAsync();
        return Ok(stats);
    }
    
    /// <summary>
    /// Searches learning memories.
    /// </summary>
    [HttpGet("learning/search")]
    public async Task<ActionResult<List<SimilarMemoryResult>>> SearchMemories([FromQuery] string query, [FromQuery] int limit = 10)
    {
        var results = await _ragStore.SearchMemoriesAsync(query, limit);
        return Ok(results);
    }
}

public class AppFeedbackRequest
{
    public AppRemovalRecommendation Recommendation { get; set; } = new();
    public bool UserApproved { get; set; }
    public string? UserReason { get; set; }
}

public class RelocationFeedbackRequest
{
    public RelocationRecommendation Recommendation { get; set; } = new();
    public bool UserApproved { get; set; }
    public string? ActualTargetDrive { get; set; }
}

public class CleanupFeedbackRequest
{
    public CleanupOpportunity Opportunity { get; set; } = new();
    public bool UserApproved { get; set; }
}
