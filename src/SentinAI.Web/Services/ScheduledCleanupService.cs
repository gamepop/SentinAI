using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NCrontab;

namespace SentinAI.Web.Services;

public interface IScheduledCleanupService
{
    Task<List<ScheduledTask>> GetScheduledTasksAsync();
    Task<ScheduledTask> CreateScheduleAsync(ScheduledTaskRequest request);
    Task<bool> UpdateScheduleAsync(string taskId, ScheduledTaskRequest request);
    Task<bool> DeleteScheduleAsync(string taskId);
    Task<bool> RunTaskNowAsync(string taskId);
    Task<List<TaskExecutionHistory>> GetTaskHistoryAsync(string taskId, int limit = 20);
}

public class ScheduledTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty; // NCrontab format
    public ScheduledTaskType TaskType { get; set; }
    public CleanupTaskConfig? CleanupConfig { get; set; }
    public DuplicateScanTaskConfig? DuplicateScanConfig { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastRunAt { get; set; }
    public DateTime? NextRunAt { get; set; }
    public TaskExecutionStatus LastStatus { get; set; } = TaskExecutionStatus.NotRun;
}

public class ScheduledTaskRequest
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty;
    public ScheduledTaskType TaskType { get; set; }
    public CleanupTaskConfig? CleanupConfig { get; set; }
    public DuplicateScanTaskConfig? DuplicateScanConfig { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public class CleanupTaskConfig
{
    public List<string> TargetPaths { get; set; } = new();
    public List<string> Categories { get; set; } = new() { "Temp", "Cache" };
    public bool AutoApproveHighConfidence { get; set; } = true;
    public double MinConfidence { get; set; } = 0.9;
    public bool DryRun { get; set; } = false;
}

public class DuplicateScanTaskConfig
{
    public string RootPath { get; set; } = "C:\\Users";
    public long MinFileSizeBytes { get; set; } = 1024 * 1024; // 1MB
    public bool AutoDeleteDuplicates { get; set; } = false;
}

public enum ScheduledTaskType
{
    Cleanup,
    DuplicateScan,
    FullSystemScan
}

public enum TaskExecutionStatus
{
    NotRun,
    Running,
    Success,
    Failed,
    Cancelled,
    Skipped
}

public class TaskExecutionHistory
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string TaskId { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public TaskExecutionStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
    public int FilesProcessed { get; set; }
    public long BytesFreed { get; set; }
    public TimeSpan Duration => CompletedAt.HasValue ? CompletedAt.Value - StartedAt : TimeSpan.Zero;
}

/// <summary>
/// Background service that executes scheduled cleanup tasks.
/// Uses NCrontab for cron expression parsing.
/// </summary>
public class ScheduledCleanupService : BackgroundService, IScheduledCleanupService
{
    private readonly ILogger<ScheduledCleanupService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, ScheduledTask> _tasks = new();
    private readonly ConcurrentDictionary<string, List<TaskExecutionHistory>> _history = new();
    private readonly SemaphoreSlim _executionLock = new(1, 1);

    public ScheduledCleanupService(
        ILogger<ScheduledCleanupService> logger,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;

        // Add default scheduled tasks
        InitializeDefaultTasks();
    }

    private void InitializeDefaultTasks()
    {
        // Daily temp cleanup at 3 AM
        var dailyCleanup = new ScheduledTask
        {
            Id = "daily-temp-cleanup",
            Name = "Daily Temp Cleanup",
            Description = "Clean temporary files daily at 3 AM",
            CronExpression = "0 3 * * *", // 3:00 AM every day
            TaskType = ScheduledTaskType.Cleanup,
            CleanupConfig = new CleanupTaskConfig
            {
                TargetPaths = new List<string>
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Temp",
                    "C:\\Windows\\Temp"
                },
                Categories = new List<string> { "Temp" },
                AutoApproveHighConfidence = true,
                MinConfidence = 0.95
            },
            IsEnabled = false // Disabled by default, user can enable
        };
        _tasks[dailyCleanup.Id] = dailyCleanup;

        // Weekly duplicate scan on Sundays at 2 AM
        var weeklyDuplicates = new ScheduledTask
        {
            Id = "weekly-duplicate-scan",
            Name = "Weekly Duplicate Scan",
            Description = "Scan for duplicate files every Sunday at 2 AM",
            CronExpression = "0 2 * * 0", // 2:00 AM every Sunday
            TaskType = ScheduledTaskType.DuplicateScan,
            DuplicateScanConfig = new DuplicateScanTaskConfig
            {
                RootPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                MinFileSizeBytes = 1024 * 1024, // 1MB
                AutoDeleteDuplicates = false
            },
            IsEnabled = false
        };
        _tasks[weeklyDuplicates.Id] = weeklyDuplicates;

        // Calculate next run times
        foreach (var task in _tasks.Values)
        {
            task.NextRunAt = CalculateNextRun(task.CronExpression);
        }
    }

    public Task<List<ScheduledTask>> GetScheduledTasksAsync()
    {
        return Task.FromResult(_tasks.Values.OrderBy(t => t.NextRunAt).ToList());
    }

    public Task<ScheduledTask> CreateScheduleAsync(ScheduledTaskRequest request)
    {
        // Validate cron expression
        try
        {
            CrontabSchedule.Parse(request.CronExpression);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Invalid cron expression: {ex.Message}", nameof(request.CronExpression));
        }

        var task = new ScheduledTask
        {
            Name = request.Name,
            Description = request.Description,
            CronExpression = request.CronExpression,
            TaskType = request.TaskType,
            CleanupConfig = request.CleanupConfig,
            DuplicateScanConfig = request.DuplicateScanConfig,
            IsEnabled = request.IsEnabled,
            NextRunAt = CalculateNextRun(request.CronExpression)
        };

        _tasks[task.Id] = task;
        _logger.LogInformation("📅 Created scheduled task: {Name} ({Id})", task.Name, task.Id);

        return Task.FromResult(task);
    }

    public Task<bool> UpdateScheduleAsync(string taskId, ScheduledTaskRequest request)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            return Task.FromResult(false);

        // Validate cron expression
        try
        {
            CrontabSchedule.Parse(request.CronExpression);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Invalid cron expression: {ex.Message}", nameof(request.CronExpression));
        }

        task.Name = request.Name;
        task.Description = request.Description;
        task.CronExpression = request.CronExpression;
        task.TaskType = request.TaskType;
        task.CleanupConfig = request.CleanupConfig;
        task.DuplicateScanConfig = request.DuplicateScanConfig;
        task.IsEnabled = request.IsEnabled;
        task.NextRunAt = CalculateNextRun(request.CronExpression);

        _logger.LogInformation("📅 Updated scheduled task: {Name} ({Id})", task.Name, task.Id);
        return Task.FromResult(true);
    }

    public Task<bool> DeleteScheduleAsync(string taskId)
    {
        var removed = _tasks.TryRemove(taskId, out var task);
        if (removed)
        {
            _history.TryRemove(taskId, out _);
            _logger.LogInformation("🗑️ Deleted scheduled task: {Name} ({Id})", task?.Name, taskId);
        }
        return Task.FromResult(removed);
    }

    public async Task<bool> RunTaskNowAsync(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            return false;

        _logger.LogInformation("▶️ Manual execution requested for task: {Name}", task.Name);
        await ExecuteTaskAsync(task, CancellationToken.None);
        return true;
    }

    public Task<List<TaskExecutionHistory>> GetTaskHistoryAsync(string taskId, int limit = 20)
    {
        if (_history.TryGetValue(taskId, out var history))
        {
            return Task.FromResult(history.OrderByDescending(h => h.StartedAt).Take(limit).ToList());
        }
        return Task.FromResult(new List<TaskExecutionHistory>());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🕐 Scheduled cleanup service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;

                foreach (var task in _tasks.Values.Where(t => t.IsEnabled))
                {
                    if (task.NextRunAt.HasValue && task.NextRunAt.Value <= now)
                    {
                        await ExecuteTaskAsync(task, stoppingToken);
                        task.NextRunAt = CalculateNextRun(task.CronExpression);
                    }
                }

                // Check every minute
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in scheduled task loop");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        _logger.LogInformation("🛑 Scheduled cleanup service stopped");
    }

    private async Task ExecuteTaskAsync(ScheduledTask task, CancellationToken cancellationToken)
    {
        if (!await _executionLock.WaitAsync(0, cancellationToken))
        {
            _logger.LogWarning("⏭️ Skipping task {Name} - another task is running", task.Name);
            return;
        }

        var history = new TaskExecutionHistory
        {
            TaskId = task.Id,
            StartedAt = DateTime.UtcNow,
            Status = TaskExecutionStatus.Running
        };

        try
        {
            _logger.LogInformation("▶️ Starting scheduled task: {Name}", task.Name);
            task.LastStatus = TaskExecutionStatus.Running;

            using var scope = _serviceProvider.CreateScope();

            switch (task.TaskType)
            {
                case ScheduledTaskType.Cleanup:
                    await ExecuteCleanupTaskAsync(task, history, scope.ServiceProvider, cancellationToken);
                    break;

                case ScheduledTaskType.DuplicateScan:
                    await ExecuteDuplicateScanTaskAsync(task, history, scope.ServiceProvider, cancellationToken);
                    break;

                case ScheduledTaskType.FullSystemScan:
                    await ExecuteFullScanTaskAsync(task, history, scope.ServiceProvider, cancellationToken);
                    break;
            }

            history.Status = TaskExecutionStatus.Success;
            task.LastStatus = TaskExecutionStatus.Success;
            _logger.LogInformation("✅ Completed task: {Name} in {Duration}", task.Name, history.Duration);
        }
        catch (OperationCanceledException)
        {
            history.Status = TaskExecutionStatus.Cancelled;
            task.LastStatus = TaskExecutionStatus.Cancelled;
            _logger.LogWarning("⚠️ Task cancelled: {Name}", task.Name);
        }
        catch (Exception ex)
        {
            history.Status = TaskExecutionStatus.Failed;
            history.ErrorMessage = ex.Message;
            task.LastStatus = TaskExecutionStatus.Failed;
            _logger.LogError(ex, "❌ Task failed: {Name}", task.Name);
        }
        finally
        {
            history.CompletedAt = DateTime.UtcNow;
            task.LastRunAt = history.StartedAt;

            // Store history
            if (!_history.TryGetValue(task.Id, out var taskHistory))
            {
                taskHistory = new List<TaskExecutionHistory>();
                _history[task.Id] = taskHistory;
            }
            taskHistory.Add(history);

            // Keep only last 100 entries
            if (taskHistory.Count > 100)
            {
                taskHistory.RemoveRange(0, taskHistory.Count - 100);
            }

            _executionLock.Release();
        }
    }

    private async Task ExecuteCleanupTaskAsync(
        ScheduledTask task,
        TaskExecutionHistory history,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var brain = services.GetRequiredService<IAgentBrain>();
        var config = task.CleanupConfig;

        if (config == null)
        {
            throw new InvalidOperationException("Cleanup task config is required");
        }

        int filesProcessed = 0;
        long bytesFreed = 0;

        foreach (var targetPath in config.TargetPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(targetPath))
            {
                _logger.LogWarning("Target path does not exist: {Path}", targetPath);
                continue;
            }

            try
            {
                var files = Directory.GetFiles(targetPath, "*", SearchOption.AllDirectories);
                var fileNames = files.Select(Path.GetFileName).Where(n => n != null).Cast<string>().ToList();

                var suggestion = await brain.AnalyzeFolderAsync(targetPath, fileNames);

                if (suggestion.SafeToDelete &&
                    suggestion.Confidence >= config.MinConfidence &&
                    config.Categories.Contains(suggestion.Category.ToString()))
                {
                    if (!config.DryRun)
                    {
                        foreach (var file in files)
                        {
                            try
                            {
                                var fileInfo = new FileInfo(file);
                                bytesFreed += fileInfo.Length;
                                fileInfo.Delete();
                                filesProcessed++;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Could not delete {File}", file);
                            }
                        }
                    }
                    else
                    {
                        filesProcessed = files.Length;
                        bytesFreed = files.Sum(f => new FileInfo(f).Length);
                        _logger.LogInformation("[DRY RUN] Would delete {Count} files, {Bytes} bytes", filesProcessed, bytesFreed);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing path {Path}", targetPath);
            }
        }

        history.FilesProcessed = filesProcessed;
        history.BytesFreed = bytesFreed;
    }

    private async Task ExecuteDuplicateScanTaskAsync(
        ScheduledTask task,
        TaskExecutionHistory history,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var duplicateService = services.GetRequiredService<IDuplicateFileService>();
        var config = task.DuplicateScanConfig;

        if (config == null)
        {
            throw new InvalidOperationException("Duplicate scan config is required");
        }

        var options = new DuplicateScanOptions
        {
            MinFileSizeBytes = config.MinFileSizeBytes
        };

        var result = await duplicateService.ScanForDuplicatesAsync(
            config.RootPath, options, null, cancellationToken);

        history.FilesProcessed = result.TotalFilesScanned;
        history.BytesFreed = result.TotalWastedBytes; // Potential savings

        _logger.LogInformation("Found {Groups} duplicate groups, {Wasted} potential savings",
            result.DuplicateGroups.Count, FormatBytes(result.TotalWastedBytes));
    }

    private async Task ExecuteFullScanTaskAsync(
        ScheduledTask task,
        TaskExecutionHistory history,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var brain = services.GetRequiredService<IAgentBrain>();

        // Scan common cleanup locations
        var scanPaths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Temp",
            "C:\\Windows\\Temp",
            Environment.GetFolderPath(Environment.SpecialFolder.InternetCache)
        };

        int totalFiles = 0;
        long totalBytes = 0;

        foreach (var path in scanPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(path)) continue;

            try
            {
                var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
                var fileNames = files.Select(Path.GetFileName).Where(n => n != null).Cast<string>().ToList();

                var suggestion = await brain.AnalyzeFolderAsync(path, fileNames);

                if (suggestion.SafeToDelete)
                {
                    totalFiles += files.Length;
                    totalBytes += files.Sum(f =>
                    {
                        try { return new FileInfo(f).Length; }
                        catch { return 0; }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error scanning {Path}", path);
            }
        }

        history.FilesProcessed = totalFiles;
        history.BytesFreed = totalBytes;
    }

    private static DateTime? CalculateNextRun(string cronExpression)
    {
        try
        {
            var schedule = CrontabSchedule.Parse(cronExpression);
            return schedule.GetNextOccurrence(DateTime.UtcNow);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }
}
