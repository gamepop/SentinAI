using Microsoft.AspNetCore.Mvc;
using SentinAI.Shared;
using SentinAI.Shared.Models;

namespace SentinAI.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AgentController : ControllerBase
{
    private readonly ILogger<AgentController> _logger;
    private readonly AgentService.AgentServiceClient? _sentinelClient;

    public AgentController(
        ILogger<AgentController> logger,
        AgentService.AgentServiceClient? sentinelClient = null)
    {
        _logger = logger;
        _sentinelClient = sentinelClient;
    }

    /// <summary>
    /// Get the status of the Sentinel Service
    /// </summary>
    [HttpGet("status")]
    public async Task<ActionResult<ServiceStatus>> GetStatus()
    {
        try
        {
            if (_sentinelClient == null)
            {
                return Ok(new ServiceStatus
                {
                    IsRunning = false,
                    IsMonitoring = false,
                    UptimeSeconds = 0,
                    PendingAnalyses = 0
                });
            }

            var request = new StatusRequest { Detailed = true };
            var status = await _sentinelClient.GetServiceStatusAsync(request);
            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get service status");
            return StatusCode(500, new { error = "Failed to contact Sentinel Service" });
        }
    }

    /// <summary>
    /// Get pending cleanup suggestions awaiting user approval
    /// </summary>
    [HttpGet("suggestions")]
    public async Task<ActionResult> GetPendingSuggestions()
    {
        try
        {
            if (_sentinelClient == null)
            {
                return Ok(new { analyses = new List<object>() });
            }

            var response = await _sentinelClient.GetPendingSuggestionsAsync(new PendingSuggestionsRequest());
            return Ok(new { analyses = response.Analyses });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get pending suggestions");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Approve a cleanup analysis
    /// </summary>
    [HttpPost("approve/{analysisId}")]
    public async Task<ActionResult> ApproveCleanup(string analysisId)
    {
        try
        {
            if (_sentinelClient == null)
            {
                return BadRequest(new { error = "Sentinel Service not connected" });
            }

            var command = new CleanupCommand
            {
                AnalysisId = analysisId,
                UserApproved = true
            };

            var result = await _sentinelClient.ExecuteCleanupAsync(command);
            
            if (result.Success)
            {
                return Ok(new
                {
                    filesDeleted = result.FilesDeleted,
                    bytesFreed = result.BytesFreed
                });
            }

            return BadRequest(new { errors = result.Errors });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to approve cleanup {AnalysisId}", analysisId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Reject a cleanup analysis
    /// </summary>
    [HttpPost("reject/{analysisId}")]
    public async Task<ActionResult> RejectCleanup(string analysisId)
    {
        _logger.LogInformation("Cleanup analysis {AnalysisId} rejected by user", analysisId);
        return Ok();
    }

    /// <summary>
    /// Trigger manual analysis of specific folders
    /// </summary>
    [HttpPost("analyze")]
    public async Task<ActionResult> TriggerAnalysis([FromBody] AnalyzeRequest request)
    {
        _logger.LogInformation("🔍 Analyze Now API called: FullSystemScan={FullScan}, Folders={Folders}",
            request.FullSystemScan,
            string.Join(", ", request.FolderPaths ?? new List<string>()));

        try
        {
            if (_sentinelClient == null)
            {
                return BadRequest(new { error = "Sentinel Service not connected" });
            }

            var triggerRequest = new TriggerAnalysisRequest
            {
                FullSystemScan = request.FullSystemScan,
                Reason = request.Reason ?? "Manual analysis from Web UI"
            };

            if (request.FolderPaths != null)
            {
                triggerRequest.FolderPaths.AddRange(request.FolderPaths);
            }

            var response = await _sentinelClient.TriggerAnalysisAsync(triggerRequest);
            
            _logger.LogInformation("📊 Analyze Now response: Accepted={Accepted}, AnalysisId={Id}, FoldersQueued={Folders}",
                response.Accepted, response.AnalysisId, response.FoldersQueued);

            return Ok(new
            {
                accepted = response.Accepted,
                analysisId = response.AnalysisId,
                foldersQueued = response.FoldersQueued,
                message = response.Message
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger analysis");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Quick system scan - triggers full system analysis
    /// </summary>
    [HttpPost("analyze/system")]
    public async Task<ActionResult> TriggerSystemAnalysis()
    {
        return await TriggerAnalysis(new AnalyzeRequest
        {
            FullSystemScan = true,
            Reason = "Full system scan from Web UI"
        });
    }
}

public record AnalyzeRequest
{
    public List<string>? FolderPaths { get; init; }
    public bool FullSystemScan { get; init; }
    public string? Reason { get; init; }
}
