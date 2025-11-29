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
    /// Enable a scheduled task
    /// </summary>
    [HttpPost("tasks/{taskId}/enable")]
    public async Task<ActionResult> EnableTask(string taskId)
    {
        var success = await _schedulerService.SetTaskEnabledAsync(taskId, true);
        if (!success)
            return NotFound(new { error = "Task not found" });

        return Ok(new { message = "Task enabled" });
    }

    /// <summary>
    /// Disable a scheduled task
    /// </summary>
    [HttpPost("tasks/{taskId}/disable")]
    public async Task<ActionResult> DisableTask(string taskId)
    {
        var success = await _schedulerService.SetTaskEnabledAsync(taskId, false);
        if (!success)
            return NotFound(new { error = "Task not found" });

        return Ok(new { message = "Task disabled" });
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

    #region Pending Review Endpoints

    /// <summary>
    /// Get all pending cleanup items awaiting user review
    /// </summary>
    [HttpGet("pending")]
    public async Task<ActionResult<List<PendingCleanupItem>>> GetPendingItems([FromQuery] string? taskId = null)
    {
        var items = await _schedulerService.GetPendingReviewItemsAsync(taskId);
        return Ok(items);
    }

    /// <summary>
    /// Get a cleanup report by history ID
    /// </summary>
    [HttpGet("reports/{historyId}")]
    public async Task<ActionResult<CleanupReport>> GetCleanupReport(string historyId)
    {
        var report = await _schedulerService.GetCleanupReportAsync(historyId);
        if (report == null)
            return NotFound(new { error = "Report not found" });
        return Ok(report);
    }

    /// <summary>
    /// Approve a pending item (deletes the file)
    /// </summary>
    [HttpPost("pending/{itemId}/approve")]
    public async Task<ActionResult> ApproveItem(string itemId)
    {
        var success = await _schedulerService.ApproveItemAsync(itemId);
        if (!success)
            return NotFound(new { error = "Item not found or already processed" });
        return Ok(new { message = "Item approved and deleted" });
    }

    /// <summary>
    /// Reject a pending item (keeps the file)
    /// </summary>
    [HttpPost("pending/{itemId}/reject")]
    public async Task<ActionResult> RejectItem(string itemId)
    {
        var success = await _schedulerService.RejectItemAsync(itemId);
        if (!success)
            return NotFound(new { error = "Item not found or already processed" });
        return Ok(new { message = "Item rejected, file kept" });
    }

    /// <summary>
    /// Approve all pending items for a task
    /// </summary>
    [HttpPost("pending/approve-all")]
    public async Task<ActionResult> ApproveAll([FromQuery] string taskId)
    {
        var count = await _schedulerService.ApproveAllPendingAsync(taskId);
        return Ok(new { message = $"Approved and deleted {count} items" });
    }

    /// <summary>
    /// Reject all pending items for a task
    /// </summary>
    [HttpPost("pending/reject-all")]
    public async Task<ActionResult> RejectAll([FromQuery] string taskId)
    {
        var count = await _schedulerService.RejectAllPendingAsync(taskId);
        return Ok(new { message = $"Rejected {count} items, files kept" });
    }

    #endregion
}

public class CronValidationRequest
{
    public string Expression { get; set; } = string.Empty;
}
