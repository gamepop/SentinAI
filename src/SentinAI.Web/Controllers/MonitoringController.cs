using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using SentinAI.Shared.Models;
using SentinAI.Web.Services;

namespace SentinAI.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MonitoringController : ControllerBase
{
    private readonly IMonitoringActivityService _activityService;
    private readonly ILogger<MonitoringController> _logger;

    public MonitoringController(
        IMonitoringActivityService activityService,
        ILogger<MonitoringController> logger)
    {
        _activityService = activityService;
        _logger = logger;
    }

    [HttpGet("activity")]
    public async Task<ActionResult<IEnumerable<MonitoringActivity>>> GetActivity(
        [FromQuery] int limit = 50,
        [FromQuery(Name = "since")] long? sinceUnixMs = null)
    {
        try
        {
            DateTimeOffset? since = null;
            if (sinceUnixMs.HasValue && sinceUnixMs.Value > 0)
            {
                since = DateTimeOffset.FromUnixTimeMilliseconds(sinceUnixMs.Value);
            }

            var activities = await _activityService.GetRecentAsync(limit, since, HttpContext.RequestAborted);
            return Ok(activities);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch monitoring activity");
            return StatusCode(502, new { error = "Sentinel activity stream unavailable" });
        }
    }
}
