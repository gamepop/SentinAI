using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using SentinAI.Shared.Models;
using SentinAI.Web.Hubs;

namespace SentinAI.Web.Services;

public interface IMonitoringActivityBroadcaster
{
    Task BroadcastAsync(IReadOnlyList<MonitoringActivity> activities, CancellationToken cancellationToken);
}

public class MonitoringActivityBroadcaster : IMonitoringActivityBroadcaster
{
    private readonly IHubContext<AgentHub> _hubContext;
    private readonly ILogger<MonitoringActivityBroadcaster> _logger;

    public MonitoringActivityBroadcaster(
        IHubContext<AgentHub> hubContext,
        ILogger<MonitoringActivityBroadcaster> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task BroadcastAsync(IReadOnlyList<MonitoringActivity> activities, CancellationToken cancellationToken)
    {
        if (activities.Count == 0)
        {
            return;
        }

        _logger.LogDebug("Broadcasting {Count} monitoring activity items", activities.Count);

        await _hubContext.Clients
            .Group(AgentHub.MonitoringGroupName)
            .SendAsync("MonitoringActivityBatch", activities, cancellationToken);
    }
}
