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
    Task<bool> SetTaskEnabledAsync(string taskId, bool enabled);
    Task<List<TaskExecutionHistory>> GetTaskHistoryAsync(string taskId, int limit = 20);

    // Pending review management
    Task<List<PendingCleanupItem>> GetPendingReviewItemsAsync(string? taskId = null);
    Task<CleanupReport?> GetCleanupReportAsync(string historyId);
    Task<bool> ApproveItemAsync(string itemId);
    Task<bool> RejectItemAsync(string itemId);
    Task<int> ApproveAllPendingAsync(string taskId);
    Task<int> RejectAllPendingAsync(string taskId);
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

/// <summary>
/// How cleanup decisions should be handled
/// </summary>
public enum CleanupApprovalMode
{
    /// <summary>AI makes the decision and deletes files automatically based on confidence threshold</summary>
    LetAIDecide,
    /// <summary>AI analyzes files but presents findings for user review before deletion</summary>
    ReviewFirst
}

public class CleanupTaskConfig
{
    public List<string> TargetPaths { get; set; } = new();
    public List<string> Categories { get; set; } = new() { "Temp", "Cache" };
    public CleanupApprovalMode ApprovalMode { get; set; } = CleanupApprovalMode.LetAIDecide;
    public double MinConfidence { get; set; } = 0.9;
    public bool DryRun { get; set; } = false;

    // Legacy property for backward compatibility
    public bool AutoApproveHighConfidence
    {
        get => ApprovalMode == CleanupApprovalMode.LetAIDecide;
        set => ApprovalMode = value ? CleanupApprovalMode.LetAIDecide : CleanupApprovalMode.ReviewFirst;
    }
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

    /// <summary>Items pending user review (when ApprovalMode is ReviewFirst)</summary>
    public List<PendingCleanupItem> PendingItems { get; set; } = new();

    /// <summary>Detailed report of what was cleaned or would be cleaned</summary>
    public CleanupReport? Report { get; set; }
}

/// <summary>
/// Represents a file or folder pending user review before deletion
/// </summary>
public class PendingCleanupItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Category { get; set; } = string.Empty;
    public double AiConfidence { get; set; }
    public string AiReasoning { get; set; } = string.Empty;
    public bool AiRecommendsDeletion { get; set; }
    public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;
    public PendingItemStatus Status { get; set; } = PendingItemStatus.Pending;
    public DateTime? ReviewedAt { get; set; }
}

public enum PendingItemStatus
{
    Pending,
    Approved,
    Rejected,
    Deleted
}

/// <summary>
/// Detailed report of a cleanup execution
/// </summary>
public class CleanupReport
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string TaskId { get; set; } = string.Empty;
    public string TaskName { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public CleanupApprovalMode ApprovalMode { get; set; }

    public int TotalFilesAnalyzed { get; set; }
    public int FilesRecommendedForDeletion { get; set; }
    public int FilesDeleted { get; set; }
    public int FilesPendingReview { get; set; }
    public int FilesSkipped { get; set; }
    public long TotalBytesFreed { get; set; }
    public long PotentialBytesToFree { get; set; }

    public List<CleanupReportItem> Items { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

public class CleanupReportItem
{
    public string FilePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Category { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public string Reason { get; set; } = string.Empty;
    public CleanupReportItemAction Action { get; set; }
}

public enum CleanupReportItemAction
{
    Deleted,
    PendingReview,
    Skipped,
    Error
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
    private readonly ConcurrentDictionary<string, PendingCleanupItem> _pendingItems = new();
    private readonly ConcurrentDictionary<string, CleanupReport> _reports = new();
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

        // Monthly downloads cleanup on the 1st at 4 AM
        var monthlyDownloads = new ScheduledTask
        {
            Id = "monthly-downloads-cleanup",
            Name = "Monthly Downloads Cleanup",
            Description = "Clean old downloads on the 1st of each month at 4 AM",
            CronExpression = "0 4 1 * *", // 4:00 AM on the 1st of every month
            TaskType = ScheduledTaskType.Cleanup,
            CleanupConfig = new CleanupTaskConfig
            {
                TargetPaths = new List<string>
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads"
                },
                Categories = new List<string> { "Downloads" },
                AutoApproveHighConfidence = false, // Require manual approval for downloads
                MinConfidence = 0.9
            },
            IsEnabled = false
        };
        _tasks[monthlyDownloads.Id] = monthlyDownloads;

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

    public Task<bool> SetTaskEnabledAsync(string taskId, bool enabled)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            return Task.FromResult(false);

        task.IsEnabled = enabled;
        if (enabled)
        {
            task.NextRunAt = CalculateNextRun(task.CronExpression);
        }

        _logger.LogInformation("{Action} scheduled task: {Name} ({Id})",
            enabled ? "✅ Enabled" : "⏸️ Disabled", task.Name, task.Id);
        return Task.FromResult(true);
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

        // Create cleanup report
        var report = new CleanupReport
        {
            TaskId = task.Id,
            TaskName = task.Name,
            ApprovalMode = config.ApprovalMode
        };

        int filesDeleted = 0;
        int filesPending = 0;
        int filesSkipped = 0;
        long bytesFreed = 0;
        long bytesPending = 0;

        foreach (var targetPath in config.TargetPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(targetPath))
            {
                _logger.LogWarning("Target path does not exist: {Path}", targetPath);
                report.Errors.Add($"Path not found: {targetPath}");
                continue;
            }

            try
            {
                var files = Directory.GetFiles(targetPath, "*", SearchOption.AllDirectories);
                report.TotalFilesAnalyzed += files.Length;

                var fileNames = files.Select(Path.GetFileName).Where(n => n != null).Cast<string>().ToList();
                var suggestion = await brain.AnalyzeFolderAsync(targetPath, fileNames);

                var categoryStr = suggestion.Category ?? "Unknown";
                var shouldClean = suggestion.SafeToDelete &&
                    suggestion.Confidence >= config.MinConfidence &&
                    (config.Categories == null || config.Categories.Count == 0 ||
                     config.Categories.Contains(categoryStr));

                if (!shouldClean)
                {
                    filesSkipped += files.Length;
                    foreach (var file in files)
                    {
                        report.Items.Add(new CleanupReportItem
                        {
                            FilePath = file,
                            SizeBytes = TryGetFileSize(file),
                            Category = categoryStr,
                            Confidence = suggestion.Confidence,
                            Reason = $"AI confidence {suggestion.Confidence:P0} below threshold or category mismatch",
                            Action = CleanupReportItemAction.Skipped
                        });
                    }
                    continue;
                }

                report.FilesRecommendedForDeletion += files.Length;

                // Handle based on approval mode
                if (config.ApprovalMode == CleanupApprovalMode.LetAIDecide)
                {
                    // Auto-delete mode
                    if (!config.DryRun)
                    {
                        foreach (var file in files)
                        {
                            try
                            {
                                var fileInfo = new FileInfo(file);
                                var size = fileInfo.Length;
                                fileInfo.Delete();
                                filesDeleted++;
                                bytesFreed += size;

                                report.Items.Add(new CleanupReportItem
                                {
                                    FilePath = file,
                                    SizeBytes = size,
                                    Category = categoryStr,
                                    Confidence = suggestion.Confidence,
                                    Reason = suggestion.Reason ?? "AI recommended deletion",
                                    Action = CleanupReportItemAction.Deleted
                                });
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Could not delete {File}", file);
                                report.Errors.Add($"Could not delete: {file}");
                                report.Items.Add(new CleanupReportItem
                                {
                                    FilePath = file,
                                    SizeBytes = TryGetFileSize(file),
                                    Category = categoryStr,
                                    Confidence = suggestion.Confidence,
                                    Reason = ex.Message,
                                    Action = CleanupReportItemAction.Error
                                });
                            }
                        }
                    }
                    else
                    {
                        foreach (var file in files)
                        {
                            var size = TryGetFileSize(file);
                            filesDeleted++;
                            bytesFreed += size;
                            report.Items.Add(new CleanupReportItem
                            {
                                FilePath = file,
                                SizeBytes = size,
                                Category = categoryStr,
                                Confidence = suggestion.Confidence,
                                Reason = "[DRY RUN] Would delete",
                                Action = CleanupReportItemAction.Deleted
                            });
                        }
                        _logger.LogInformation("[DRY RUN] Would delete {Count} files, {Bytes} bytes",
                            files.Length, bytesFreed);
                    }
                }
                else
                {
                    // Review mode - create pending items for user approval
                    foreach (var file in files)
                    {
                        var size = TryGetFileSize(file);
                        var pendingItem = new PendingCleanupItem
                        {
                            FilePath = file,
                            FileName = Path.GetFileName(file),
                            SizeBytes = size,
                            Category = categoryStr,
                            AiConfidence = suggestion.Confidence,
                            AiReasoning = suggestion.Reason ?? "AI recommends deletion based on file analysis",
                            AiRecommendsDeletion = true,
                            Status = PendingItemStatus.Pending
                        };

                        _pendingItems[pendingItem.Id] = pendingItem;
                        history.PendingItems.Add(pendingItem);
                        filesPending++;
                        bytesPending += size;

                        report.Items.Add(new CleanupReportItem
                        {
                            FilePath = file,
                            SizeBytes = size,
                            Category = categoryStr,
                            Confidence = suggestion.Confidence,
                            Reason = suggestion.Reason ?? "Pending user review",
                            Action = CleanupReportItemAction.PendingReview
                        });
                    }

                    _logger.LogInformation("📋 Created {Count} items pending review for task {TaskName}",
                        filesPending, task.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing path {Path}", targetPath);
                report.Errors.Add($"Error processing {targetPath}: {ex.Message}");
            }
        }

        // Update report totals
        report.FilesDeleted = filesDeleted;
        report.FilesPendingReview = filesPending;
        report.FilesSkipped = filesSkipped;
        report.TotalBytesFreed = bytesFreed;
        report.PotentialBytesToFree = bytesPending;

        // Store the report
        _reports[history.Id] = report;
        history.Report = report;
        history.FilesProcessed = filesDeleted + filesPending;
        history.BytesFreed = bytesFreed;
    }

    private static long TryGetFileSize(string filePath)
    {
        try { return new FileInfo(filePath).Length; }
        catch { return 0; }
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

    #region Pending Review Management

    public Task<List<PendingCleanupItem>> GetPendingReviewItemsAsync(string? taskId = null)
    {
        var items = _pendingItems.Values
            .Where(p => p.Status == PendingItemStatus.Pending)
            .OrderByDescending(p => p.AnalyzedAt)
            .ToList();

        return Task.FromResult(items);
    }

    public Task<CleanupReport?> GetCleanupReportAsync(string historyId)
    {
        _reports.TryGetValue(historyId, out var report);
        return Task.FromResult(report);
    }

    public async Task<bool> ApproveItemAsync(string itemId)
    {
        if (!_pendingItems.TryGetValue(itemId, out var item))
            return false;

        if (item.Status != PendingItemStatus.Pending)
            return false;

        try
        {
            if (File.Exists(item.FilePath))
            {
                File.Delete(item.FilePath);
                item.Status = PendingItemStatus.Deleted;
                _logger.LogInformation("✅ Approved and deleted: {File}", item.FilePath);
            }
            else
            {
                item.Status = PendingItemStatus.Deleted; // Already gone
                _logger.LogWarning("File already deleted: {File}", item.FilePath);
            }
            item.ReviewedAt = DateTime.UtcNow;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete approved item: {File}", item.FilePath);
            return false;
        }
    }

    public Task<bool> RejectItemAsync(string itemId)
    {
        if (!_pendingItems.TryGetValue(itemId, out var item))
            return Task.FromResult(false);

        if (item.Status != PendingItemStatus.Pending)
            return Task.FromResult(false);

        item.Status = PendingItemStatus.Rejected;
        item.ReviewedAt = DateTime.UtcNow;
        _logger.LogInformation("❌ Rejected cleanup item: {File}", item.FilePath);

        return Task.FromResult(true);
    }

    public async Task<int> ApproveAllPendingAsync(string taskId)
    {
        var pendingItems = _pendingItems.Values
            .Where(p => p.Status == PendingItemStatus.Pending)
            .ToList();

        int approved = 0;
        foreach (var item in pendingItems)
        {
            if (await ApproveItemAsync(item.Id))
                approved++;
        }

        _logger.LogInformation("✅ Approved all: {Count} items deleted", approved);
        return approved;
    }

    public Task<int> RejectAllPendingAsync(string taskId)
    {
        var pendingItems = _pendingItems.Values
            .Where(p => p.Status == PendingItemStatus.Pending)
            .ToList();

        int rejected = 0;
        foreach (var item in pendingItems)
        {
            item.Status = PendingItemStatus.Rejected;
            item.ReviewedAt = DateTime.UtcNow;
            rejected++;
        }

        _logger.LogInformation("❌ Rejected all: {Count} items kept", rejected);
        return Task.FromResult(rejected);
    }

    #endregion
}
