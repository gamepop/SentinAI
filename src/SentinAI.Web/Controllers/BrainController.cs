using Microsoft.AspNetCore.Mvc;
using SentinAI.Web.Services;
using SentinAI.Web.Services.Rag;

namespace SentinAI.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BrainController : ControllerBase
{
    private readonly ILogger<BrainController> _logger;
    private readonly IModelDownloadService _modelDownloadService;
    private readonly IAgentBrain _brain;
    private readonly IRagStore _ragStore;

    public BrainController(
        ILogger<BrainController> logger,
        IModelDownloadService modelDownloadService,
        IAgentBrain brain,
        IRagStore ragStore)
    {
        _logger = logger;
        _modelDownloadService = modelDownloadService;
        _brain = brain;
        _ragStore = ragStore;
    }

    /// <summary>
    /// Get the current Brain status and statistics
    /// </summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var stats = _brain.GetStats();
        var modelPath = _modelDownloadService.GetModelPath();
        var modelDownloaded = _modelDownloadService.IsModelDownloaded();
        var executionProvider = _modelDownloadService.GetExecutionProvider();

        return Ok(new
        {
            isReady = _brain.IsReady,
            isModelLoaded = _brain.IsModelLoaded,
            mode = _brain.IsModelLoaded ? "AI + Heuristics" : "Heuristics Only",
            executionProvider,
            modelPath,
            modelDownloaded,
            statistics = new
            {
                totalAnalyses = stats.TotalAnalyses,
                modelDecisions = stats.ModelDecisions,
                heuristicOnly = stats.HeuristicOnly,
                safeToDeleteCount = stats.SafeToDeleteCount
            }
        });
    }

    [HttpPost("refresh-model")]
    public async Task<IActionResult> RefreshModel(CancellationToken cancellationToken)
    {
        try
        {
            var modelPath = _modelDownloadService.GetModelPath();
            _logger.LogInformation("Manual model refresh requested via API. Cache path: {Path}.", modelPath);

            await _modelDownloadService.DownloadModelAsync(cancellationToken, forceRedownload: true);

            var initialized = await _brain.InitializeAsync(modelPath);
            _logger.LogInformation("Manual model refresh completed. Brain initialized: {Initialized}.", initialized);

            return Ok(new
            {
                initialized,
                modelPath
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual model refresh failed.");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get RAG store status and statistics
    /// </summary>
    [HttpGet("memory/status")]
    public IActionResult GetMemoryStatus()
    {
        return Ok(new
        {
            enabled = _ragStore.IsEnabled,
            provider = "Weaviate",
            description = "Long-term memory for analysis decisions"
        });
    }

    /// <summary>
    /// Query memories for a specific session
    /// </summary>
    [HttpGet("memory/query")]
    public async Task<IActionResult> QueryMemories(
        [FromQuery] string sessionId,
        [FromQuery] string? query,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (!_ragStore.IsEnabled)
        {
            return Ok(new { enabled = false, memories = Array.Empty<object>(), message = "RAG store is disabled" });
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return BadRequest(new { error = "sessionId is required. Use /memory/search for cross-session queries." });
        }

        try
        {
            var searchQuery = string.IsNullOrWhiteSpace(query) ? "cleanup analysis decision" : query;
            var memories = await _ragStore.QueryAsync(sessionId, searchQuery, limit, cancellationToken);

            return Ok(new
            {
                enabled = true,
                sessionId,
                query = searchQuery,
                count = memories.Count,
                memories = memories.Select(m => new
                {
                    sessionId = m.SessionId,
                    content = m.Content,
                    timestamp = m.Timestamp,
                    score = m.Score,
                    metadata = m.Metadata
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query memories");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Semantic search across ALL memories (no session filter)
    /// </summary>
    [HttpGet("memory/search")]
    public async Task<IActionResult> SearchMemories(
        [FromQuery] string query,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (!_ragStore.IsEnabled)
        {
            return Ok(new { enabled = false, memories = Array.Empty<object>(), message = "RAG store is disabled" });
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest(new { error = "query is required for semantic search" });
        }

        try
        {
            // Pass null sessionId to search across all sessions
            var memories = await _ragStore.QueryAsync(null, query, limit, cancellationToken);

            return Ok(new
            {
                enabled = true,
                query,
                count = memories.Count,
                memories = memories.Select(m => new
                {
                    sessionId = m.SessionId,
                    content = m.Content,
                    timestamp = m.Timestamp,
                    score = m.Score,
                    metadata = m.Metadata
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search memories");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get all recent memories (for display purposes)
    /// </summary>
    [HttpGet("memory/recent")]
    public async Task<IActionResult> GetRecentMemories(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (!_ragStore.IsEnabled)
        {
            return Ok(new { enabled = false, memories = Array.Empty<object>(), message = "RAG store is disabled" });
        }

        try
        {
            // Get all recent memories regardless of session
            var memories = await _ragStore.GetAllRecentAsync(limit, cancellationToken);

            return Ok(new
            {
                enabled = true,
                count = memories.Count,
                memories = memories.Select(m => new
                {
                    sessionId = m.SessionId,
                    content = m.Content,
                    timestamp = m.Timestamp,
                    score = m.Score,
                    metadata = m.Metadata
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get recent memories");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Clear all memories for a session
    /// </summary>
    [HttpDelete("memory/{sessionId}")]
    public async Task<IActionResult> ClearSessionMemory(string sessionId, CancellationToken cancellationToken)
    {
        if (!_ragStore.IsEnabled)
        {
            return BadRequest(new { error = "RAG store is disabled" });
        }

        try
        {
            await _ragStore.DeleteSessionAsync(sessionId, cancellationToken);
            return Ok(new { message = $"Memories cleared for session: {sessionId}" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear memories for session {SessionId}", sessionId);
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
