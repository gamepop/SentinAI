using Microsoft.AspNetCore.Mvc;
using SentinAI.Web.Services;

namespace SentinAI.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SchedulerController : ControllerBase
{
    private readonly IScheduledCleanupService _schedulerService;
    private readonly ILogger<SchedulerController> _logger;

    public SchedulerController(
        IScheduledCleanupService schedulerService,
        ILogger<SchedulerController> logger)
    {
        _schedulerService = schedulerService;
        _logger = logger;
    }

    /// <summary>
    /// Get all scheduled tasks
    /// </summary>
    [HttpGet("tasks")]
    public async Task<ActionResult<List<ScheduledTask>>> GetTasks()
    {
        var tasks = await _schedulerService.GetScheduledTasksAsync();
        return Ok(tasks);
    }

    /// <summary>
    /// Create a new scheduled task
    /// </summary>
    [HttpPost("tasks")]
    public async Task<ActionResult<ScheduledTask>> CreateTask([FromBody] ScheduledTaskRequest request)
    {
        try
        {
            var task = await _schedulerService.CreateScheduleAsync(request);
            return CreatedAtAction(nameof(GetTasks), new { id = task.Id }, task);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Update an existing scheduled task
    /// </summary>
    [HttpPut("tasks/{taskId}")]
    public async Task<ActionResult> UpdateTask(string taskId, [FromBody] ScheduledTaskRequest request)
    {
        try
        {
            var success = await _schedulerService.UpdateScheduleAsync(taskId, request);
            if (!success)
                return NotFound(new { error = "Task not found" });

            return Ok(new { message = "Task updated" });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Delete a scheduled task
    /// </summary>
    [HttpDelete("tasks/{taskId}")]
    public async Task<ActionResult> DeleteTask(string taskId)
    {
        var success = await _schedulerService.DeleteScheduleAsync(taskId);
        if (!success)
            return NotFound(new { error = "Task not found" });

        return Ok(new { message = "Task deleted" });
    }

    /// <summary>
    /// Run a task immediately
    /// </summary>
    [HttpPost("tasks/{taskId}/run")]
    public async Task<ActionResult> RunTask(string taskId)
    {
        var success = await _schedulerService.RunTaskNowAsync(taskId);
        if (!success)
            return NotFound(new { error = "Task not found" });

        return Ok(new { message = "Task started" });
    }

    /// <summary>
    /// Get task execution history
    /// </summary>
    [HttpGet("tasks/{taskId}/history")]
    public async Task<ActionResult<List<TaskExecutionHistory>>> GetTaskHistory(string taskId, [FromQuery] int limit = 20)
    {
        var history = await _schedulerService.GetTaskHistoryAsync(taskId, limit);
        return Ok(history);
    }

    /// <summary>
    /// Validate a cron expression
    /// </summary>
    [HttpPost("validate-cron")]
    public ActionResult ValidateCron([FromBody] CronValidationRequest request)
    {
        try
        {
            var schedule = NCrontab.CrontabSchedule.Parse(request.Expression);
            var nextRuns = new List<string>();

            var next = DateTime.UtcNow;
            for (int i = 0; i < 5; i++)
            {
                next = schedule.GetNextOccurrence(next);
                nextRuns.Add(next.ToString("yyyy-MM-dd HH:mm:ss UTC"));
            }

            return Ok(new
            {
                valid = true,
                nextRuns
            });
        }
        catch (Exception ex)
        {
            return Ok(new
            {
                valid = false,
                error = ex.Message
            });
        }
    }
}

public class CronValidationRequest
{
    public string Expression { get; set; } = string.Empty;
}
