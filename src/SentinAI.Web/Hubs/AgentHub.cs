using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace SentinAI.Web.Hubs;

/// <summary>
/// SignalR hub for real-time updates from the Sentinel Service
/// </summary>
public class AgentHub : Hub
{
    private readonly ILogger<AgentHub> _logger;

    public const string MonitoringGroupName = "monitoring-activity";

    public AgentHub(ILogger<AgentHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Called by clients to subscribe to updates
    /// </summary>
    public async Task SubscribeToUpdates()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "agents");
        _logger.LogInformation("Client {ConnectionId} subscribed to updates", Context.ConnectionId);
    }

    /// <summary>
    /// Called by clients to receive monitoring activity batches.
    /// </summary>
    public async Task SubscribeToMonitoring()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, MonitoringGroupName);
        _logger.LogInformation("Client {ConnectionId} subscribed to monitoring activity", Context.ConnectionId);
    }
}

/// <summary>
/// Service to broadcast events to connected clients
/// </summary>
public interface IAgentNotificationService
{
    Task NotifyNewSuggestionsAsync(string analysisId, int suggestionCount);
    Task NotifyCleanupProgressAsync(string analysisId, int filesProcessed, int totalFiles);
    Task NotifyCleanupCompletedAsync(string analysisId, int filesDeleted, long bytesFreed);
    Task NotifyServiceStatusChangedAsync(bool isRunning, bool isMonitoring);
}

public class AgentNotificationService : IAgentNotificationService
{
    private readonly IHubContext<AgentHub> _hubContext;
    private readonly ILogger<AgentNotificationService> _logger;

    public AgentNotificationService(
        IHubContext<AgentHub> hubContext,
        ILogger<AgentNotificationService> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task NotifyNewSuggestionsAsync(string analysisId, int suggestionCount)
    {
        _logger.LogInformation("Broadcasting new suggestions: {AnalysisId}, Count: {Count}", analysisId, suggestionCount);
        
        await _hubContext.Clients.Group("agents").SendAsync("NewSuggestions", new
        {
            analysisId,
            suggestionCount,
            timestamp = DateTime.UtcNow
        });
    }

    public async Task NotifyCleanupProgressAsync(string analysisId, int filesProcessed, int totalFiles)
    {
        await _hubContext.Clients.Group("agents").SendAsync("CleanupProgress", new
        {
            analysisId,
            filesProcessed,
            totalFiles,
            progress = (double)filesProcessed / totalFiles
        });
    }

    public async Task NotifyCleanupCompletedAsync(string analysisId, int filesDeleted, long bytesFreed)
    {
        _logger.LogInformation("Broadcasting cleanup completed: {AnalysisId}", analysisId);
        
        await _hubContext.Clients.Group("agents").SendAsync("CleanupCompleted", new
        {
            analysisId,
            filesDeleted,
            bytesFreed,
            timestamp = DateTime.UtcNow
        });
    }

    public async Task NotifyServiceStatusChangedAsync(bool isRunning, bool isMonitoring)
    {
        await _hubContext.Clients.Group("agents").SendAsync("ServiceStatusChanged", new
        {
            isRunning,
            isMonitoring,
            timestamp = DateTime.UtcNow
        });
    }
}
