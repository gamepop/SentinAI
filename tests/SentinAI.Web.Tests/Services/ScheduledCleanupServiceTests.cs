using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SentinAI.Web.Services;

namespace SentinAI.Web.Tests.Services;

public class ScheduledCleanupServiceTests
{
    private readonly Mock<ILogger<ScheduledCleanupService>> _loggerMock;
    private readonly Mock<IServiceProvider> _serviceProviderMock;
    private readonly ScheduledCleanupService _service;

    public ScheduledCleanupServiceTests()
    {
        _loggerMock = new Mock<ILogger<ScheduledCleanupService>>();
        _serviceProviderMock = new Mock<IServiceProvider>();
        _service = new ScheduledCleanupService(_loggerMock.Object, _serviceProviderMock.Object);
    }

    [Fact]
    public async Task GetScheduledTasksAsync_ReturnsDefaultTasks()
    {
        // Act
        var tasks = await _service.GetScheduledTasksAsync();

        // Assert
        Assert.NotNull(tasks);
        Assert.Equal(2, tasks.Count); // Default: daily temp cleanup + weekly duplicate scan
    }

    [Fact]
    public async Task GetScheduledTasksAsync_DefaultTasksAreDisabled()
    {
        // Act
        var tasks = await _service.GetScheduledTasksAsync();

        // Assert
        Assert.All(tasks, t => Assert.False(t.IsEnabled));
    }

    [Fact]
    public async Task CreateScheduleAsync_CreatesNewTask()
    {
        // Arrange
        var request = new ScheduledTaskRequest
        {
            Name = "Test Task",
            Description = "A test scheduled task",
            CronExpression = "0 0 * * *", // Daily at midnight
            TaskType = ScheduledTaskType.Cleanup,
            IsEnabled = true
        };

        // Act
        var task = await _service.CreateScheduleAsync(request);

        // Assert
        Assert.NotNull(task);
        Assert.Equal("Test Task", task.Name);
        Assert.Equal("A test scheduled task", task.Description);
        Assert.Equal("0 0 * * *", task.CronExpression);
        Assert.Equal(ScheduledTaskType.Cleanup, task.TaskType);
        Assert.True(task.IsEnabled);
        Assert.NotNull(task.Id);
    }

    [Fact]
    public async Task CreateScheduleAsync_GeneratesUniqueIds()
    {
        // Arrange
        var request = new ScheduledTaskRequest
        {
            Name = "Task",
            CronExpression = "0 0 * * *",
            TaskType = ScheduledTaskType.Cleanup
        };

        // Act
        var task1 = await _service.CreateScheduleAsync(request);
        var task2 = await _service.CreateScheduleAsync(request);

        // Assert
        Assert.NotEqual(task1.Id, task2.Id);
    }

    [Fact]
    public async Task CreateScheduleAsync_CalculatesNextRunTime()
    {
        // Arrange
        var request = new ScheduledTaskRequest
        {
            Name = "Test",
            CronExpression = "0 0 * * *", // Daily at midnight
            TaskType = ScheduledTaskType.Cleanup
        };

        // Act
        var task = await _service.CreateScheduleAsync(request);

        // Assert
        Assert.NotNull(task.NextRunAt);
        Assert.True(task.NextRunAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task CreateScheduleAsync_InvalidCron_ThrowsArgumentException()
    {
        // Arrange
        var request = new ScheduledTaskRequest
        {
            Name = "Invalid",
            CronExpression = "invalid cron expression",
            TaskType = ScheduledTaskType.Cleanup
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.CreateScheduleAsync(request));
    }

    [Fact]
    public async Task UpdateScheduleAsync_UpdatesExistingTask()
    {
        // Arrange
        var createRequest = new ScheduledTaskRequest
        {
            Name = "Original",
            CronExpression = "0 0 * * *",
            TaskType = ScheduledTaskType.Cleanup
        };
        var task = await _service.CreateScheduleAsync(createRequest);

        var updateRequest = new ScheduledTaskRequest
        {
            Name = "Updated",
            Description = "New description",
            CronExpression = "0 12 * * *",
            TaskType = ScheduledTaskType.DuplicateScan,
            IsEnabled = true
        };

        // Act
        var result = await _service.UpdateScheduleAsync(task.Id, updateRequest);

        // Assert
        Assert.True(result);

        var tasks = await _service.GetScheduledTasksAsync();
        var updatedTask = tasks.FirstOrDefault(t => t.Id == task.Id);

        Assert.NotNull(updatedTask);
        Assert.Equal("Updated", updatedTask.Name);
        Assert.Equal("New description", updatedTask.Description);
        Assert.Equal("0 12 * * *", updatedTask.CronExpression);
        Assert.Equal(ScheduledTaskType.DuplicateScan, updatedTask.TaskType);
        Assert.True(updatedTask.IsEnabled);
    }

    [Fact]
    public async Task UpdateScheduleAsync_NonexistentTask_ReturnsFalse()
    {
        // Arrange
        var request = new ScheduledTaskRequest
        {
            Name = "Test",
            CronExpression = "0 0 * * *",
            TaskType = ScheduledTaskType.Cleanup
        };

        // Act
        var result = await _service.UpdateScheduleAsync("nonexistent-id", request);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task UpdateScheduleAsync_InvalidCron_ThrowsArgumentException()
    {
        // Arrange
        var createRequest = new ScheduledTaskRequest
        {
            Name = "Test",
            CronExpression = "0 0 * * *",
            TaskType = ScheduledTaskType.Cleanup
        };
        var task = await _service.CreateScheduleAsync(createRequest);

        var updateRequest = new ScheduledTaskRequest
        {
            Name = "Updated",
            CronExpression = "invalid",
            TaskType = ScheduledTaskType.Cleanup
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.UpdateScheduleAsync(task.Id, updateRequest));
    }

    [Fact]
    public async Task DeleteScheduleAsync_DeletesExistingTask()
    {
        // Arrange
        var request = new ScheduledTaskRequest
        {
            Name = "To Delete",
            CronExpression = "0 0 * * *",
            TaskType = ScheduledTaskType.Cleanup
        };
        var task = await _service.CreateScheduleAsync(request);

        // Act
        var result = await _service.DeleteScheduleAsync(task.Id);

        // Assert
        Assert.True(result);

        var tasks = await _service.GetScheduledTasksAsync();
        Assert.DoesNotContain(tasks, t => t.Id == task.Id);
    }

    [Fact]
    public async Task DeleteScheduleAsync_NonexistentTask_ReturnsFalse()
    {
        // Act
        var result = await _service.DeleteScheduleAsync("nonexistent-id");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task GetTaskHistoryAsync_ReturnsEmptyForNewTask()
    {
        // Arrange
        var request = new ScheduledTaskRequest
        {
            Name = "New Task",
            CronExpression = "0 0 * * *",
            TaskType = ScheduledTaskType.Cleanup
        };
        var task = await _service.CreateScheduleAsync(request);

        // Act
        var history = await _service.GetTaskHistoryAsync(task.Id);

        // Assert
        Assert.Empty(history);
    }

    [Fact]
    public async Task GetTaskHistoryAsync_NonexistentTask_ReturnsEmptyList()
    {
        // Act
        var history = await _service.GetTaskHistoryAsync("nonexistent-id");

        // Assert
        Assert.Empty(history);
    }

    [Fact]
    public async Task CreateScheduleAsync_WithCleanupConfig_SetsConfiguration()
    {
        // Arrange
        var request = new ScheduledTaskRequest
        {
            Name = "Cleanup Task",
            CronExpression = "0 0 * * *",
            TaskType = ScheduledTaskType.Cleanup,
            CleanupConfig = new CleanupTaskConfig
            {
                TargetPaths = new List<string> { @"C:\Temp", @"C:\Cache" },
                Categories = new List<string> { "Temp", "Cache" },
                AutoApproveHighConfidence = true,
                MinConfidence = 0.9,
                DryRun = true
            }
        };

        // Act
        var task = await _service.CreateScheduleAsync(request);

        // Assert
        Assert.NotNull(task.CleanupConfig);
        Assert.Equal(2, task.CleanupConfig.TargetPaths.Count);
        Assert.Equal(2, task.CleanupConfig.Categories.Count);
        Assert.True(task.CleanupConfig.AutoApproveHighConfidence);
        Assert.Equal(0.9, task.CleanupConfig.MinConfidence);
        Assert.True(task.CleanupConfig.DryRun);
    }

    [Fact]
    public async Task CreateScheduleAsync_WithDuplicateScanConfig_SetsConfiguration()
    {
        // Arrange
        var request = new ScheduledTaskRequest
        {
            Name = "Duplicate Scan",
            CronExpression = "0 2 * * 0",
            TaskType = ScheduledTaskType.DuplicateScan,
            DuplicateScanConfig = new DuplicateScanTaskConfig
            {
                RootPath = @"C:\Users",
                MinFileSizeBytes = 1024 * 1024,
                AutoDeleteDuplicates = false
            }
        };

        // Act
        var task = await _service.CreateScheduleAsync(request);

        // Assert
        Assert.NotNull(task.DuplicateScanConfig);
        Assert.Equal(@"C:\Users", task.DuplicateScanConfig.RootPath);
        Assert.Equal(1024 * 1024, task.DuplicateScanConfig.MinFileSizeBytes);
        Assert.False(task.DuplicateScanConfig.AutoDeleteDuplicates);
    }

    [Theory]
    [InlineData("0 0 * * *")] // Every day at midnight
    [InlineData("0 3 * * *")] // Every day at 3 AM
    [InlineData("0 0 * * 0")] // Every Sunday at midnight
    [InlineData("0 0 1 * *")] // First day of every month
    [InlineData("*/5 * * * *")] // Every 5 minutes
    public async Task CreateScheduleAsync_AcceptsValidCronExpressions(string cron)
    {
        // Arrange
        var request = new ScheduledTaskRequest
        {
            Name = "Cron Test",
            CronExpression = cron,
            TaskType = ScheduledTaskType.Cleanup
        };

        // Act
        var task = await _service.CreateScheduleAsync(request);

        // Assert
        Assert.NotNull(task);
        Assert.Equal(cron, task.CronExpression);
    }
}

public class ScheduledTaskModelTests
{
    [Fact]
    public void ScheduledTask_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var task = new ScheduledTask();

        // Assert
        Assert.False(string.IsNullOrEmpty(task.Id));
        Assert.Equal(string.Empty, task.Name);
        Assert.Equal(string.Empty, task.Description);
        Assert.Equal(string.Empty, task.CronExpression);
        Assert.Equal(ScheduledTaskType.Cleanup, task.TaskType);
        Assert.Null(task.CleanupConfig);
        Assert.Null(task.DuplicateScanConfig);
        Assert.True(task.IsEnabled);
        Assert.Null(task.LastRunAt);
        Assert.Null(task.NextRunAt);
        Assert.Equal(TaskExecutionStatus.NotRun, task.LastStatus);
    }

    [Fact]
    public void TaskExecutionHistory_Duration_CalculatesCorrectly()
    {
        // Arrange
        var history = new TaskExecutionHistory
        {
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = DateTime.UtcNow
        };

        // Assert
        Assert.True(history.Duration.TotalMinutes >= 4.9 && history.Duration.TotalMinutes <= 5.1);
    }

    [Fact]
    public void TaskExecutionHistory_Duration_ReturnsZeroWhenNotCompleted()
    {
        // Arrange
        var history = new TaskExecutionHistory
        {
            StartedAt = DateTime.UtcNow,
            CompletedAt = null
        };

        // Assert
        Assert.Equal(TimeSpan.Zero, history.Duration);
    }
}
