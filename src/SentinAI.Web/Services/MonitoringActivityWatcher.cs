using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using SentinAI.Shared.Models;

namespace SentinAI.Web.Services;

public class MonitoringActivityWatcher : BackgroundService
{
    private readonly IMonitoringActivityService _activityService;
    private readonly IMonitoringActivityBroadcaster _broadcaster;
    private readonly ILogger<MonitoringActivityWatcher> _logger;
    private readonly TimeSpan _pollInterval;

    private long _lastTimestampMs;
    private readonly HashSet<string> _lastTimestampIds = new(StringComparer.OrdinalIgnoreCase);

    public MonitoringActivityWatcher(
        IMonitoringActivityService activityService,
        IMonitoringActivityBroadcaster broadcaster,
        ILogger<MonitoringActivityWatcher> logger)
    {
        _activityService = activityService;
        _broadcaster = broadcaster;
        _logger = logger;
        _pollInterval = TimeSpan.FromSeconds(2);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Monitoring activity watcher started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Monitoring activity poll failed");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Monitoring activity watcher stopped");
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var since = _lastTimestampMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(_lastTimestampMs)
            : null as DateTimeOffset?;

        var activities = await _activityService.GetRecentAsync(200, since, cancellationToken).ConfigureAwait(false);

        if (activities.Count == 0)
        {
            return;
        }

        var newItems = FilterNewActivities(activities);
        if (newItems.Count == 0)
        {
            return;
        }

        await _broadcaster.BroadcastAsync(newItems, cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<MonitoringActivity> FilterNewActivities(IReadOnlyList<MonitoringActivity> activities)
    {
        var ordered = activities.OrderBy(a => a.Timestamp).ToList();
        var result = new List<MonitoringActivity>(ordered.Count);

        foreach (var activity in ordered)
        {
            var timestampMs = activity.Timestamp.ToUnixTimeMilliseconds();

            if (timestampMs < _lastTimestampMs)
            {
                continue;
            }

            if (timestampMs > _lastTimestampMs)
            {
                _lastTimestampMs = timestampMs;
                _lastTimestampIds.Clear();
            }

            if (_lastTimestampIds.Contains(activity.Id))
            {
                continue;
            }

            _lastTimestampIds.Add(activity.Id);
            result.Add(activity);
        }

        return result;
    }
}
