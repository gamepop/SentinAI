using Microsoft.AspNetCore.Mvc;
using SentinAI.Web.Services;

namespace SentinAI.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BrainController : ControllerBase
{
    private readonly ILogger<BrainController> _logger;
    private readonly IModelDownloadService _modelDownloadService;
    private readonly IAgentBrain _brain;

    public BrainController(
        ILogger<BrainController> logger,
        IModelDownloadService modelDownloadService,
        IAgentBrain brain)
    {
        _logger = logger;
        _modelDownloadService = modelDownloadService;
        _brain = brain;
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
}
