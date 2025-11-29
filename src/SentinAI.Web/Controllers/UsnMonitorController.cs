using Microsoft.AspNetCore.Mvc;
using SentinAI.Web.Services;

namespace SentinAI.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UsnMonitorController : ControllerBase
{
    private readonly IUsnJournalMonitorService _usnService;
    private readonly ILogger<UsnMonitorController> _logger;

    public UsnMonitorController(
        IUsnJournalMonitorService usnService,
        ILogger<UsnMonitorController> logger)
    {
        _usnService = usnService;
        _logger = logger;
    }

    /// <summary>
    /// Get current monitoring status and statistics
    /// </summary>
    [HttpGet("status")]
    public ActionResult<UsnMonitoringStats> GetStatus()
    {
        return Ok(_usnService.GetStats());
    }

    /// <summary>
    /// Start USN Journal monitoring for a drive
    /// </summary>
    [HttpPost("start")]
    public async Task<ActionResult> StartMonitoring([FromBody] StartMonitoringRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DrivePath))
        {
            return BadRequest(new { error = "DrivePath is required" });
        }

        try
        {
            await _usnService.StartMonitoringAsync(request.DrivePath);
            return Ok(new
            {
                message = $"Started monitoring {request.DrivePath}",
                status = _usnService.GetStats()
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Failed to start USN monitoring");
            return StatusCode(500, new
            {
                error = ex.Message,
                hint = "Run the application as Administrator to access USN Journal"
            });
        }
    }

    /// <summary>
    /// Stop USN Journal monitoring
    /// </summary>
    [HttpPost("stop")]
    public async Task<ActionResult> StopMonitoring()
    {
        await _usnService.StopMonitoringAsync();
        return Ok(new { message = "Monitoring stopped" });
    }

    /// <summary>
    /// Get recent USN events
    /// </summary>
    [HttpGet("events")]
    public ActionResult<List<object>> GetRecentEvents([FromQuery] int count = 100)
    {
        var events = _usnService.GetRecentEvents(count);

        return Ok(events.Select(e => new
        {
            e.Usn,
            e.FileName,
            e.FullPath,
            Reason = e.Reason.ToString(),
            PrimaryReason = GetPrimaryReason(e.Reason),
            e.FileSize,
            Timestamp = e.Timestamp.ToString("O"),
            IsDirectory = (e.Attributes & FileAttributes.Directory) != 0
        }).ToList());
    }

    /// <summary>
    /// Stream events via Server-Sent Events (SSE)
    /// </summary>
    [HttpGet("stream")]
    public async Task StreamEvents(CancellationToken cancellationToken)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        await foreach (var entry in _usnService.GetEventsStreamAsync(cancellationToken))
        {
            var data = System.Text.Json.JsonSerializer.Serialize(new
            {
                entry.Usn,
                entry.FileName,
                entry.FullPath,
                Reason = entry.Reason.ToString(),
                PrimaryReason = GetPrimaryReason(entry.Reason),
                entry.FileSize,
                Timestamp = entry.Timestamp.ToString("O")
            });

            await Response.WriteAsync($"data: {data}\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }
    }

    private static string GetPrimaryReason(SentinAI.Shared.Models.UsnReason reason)
    {
        if (reason.HasFlag(SentinAI.Shared.Models.UsnReason.FileCreate)) return "Created";
        if (reason.HasFlag(SentinAI.Shared.Models.UsnReason.FileDelete)) return "Deleted";
        if (reason.HasFlag(SentinAI.Shared.Models.UsnReason.RenameNewName)) return "Renamed";
        if (reason.HasFlag(SentinAI.Shared.Models.UsnReason.DataOverwrite)) return "Modified";
        if (reason.HasFlag(SentinAI.Shared.Models.UsnReason.Close)) return "Closed";
        return "Other";
    }
}

public class StartMonitoringRequest
{
    public string DrivePath { get; set; } = "C:";
}
