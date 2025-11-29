using SentinAI.Shared.Models;
using System.Collections.Generic;
using System.Linq;

namespace SentinAI.SentinelService.Services;

public interface IMonitoringActivityStore
{
    void Add(MonitoringActivity activity);
    IReadOnlyList<MonitoringActivity> Snapshot(int limit, DateTimeOffset? since = null);
}

public class MonitoringActivityStore : IMonitoringActivityStore
{
    private const int MaxItems = 512;
    private readonly LinkedList<MonitoringActivity> _buffer = new();
    private readonly object _gate = new();

    public void Add(MonitoringActivity activity)
    {
        lock (_gate)
        {
            _buffer.AddFirst(activity);
            while (_buffer.Count > MaxItems)
            {
                _buffer.RemoveLast();
            }
        }
    }

    public IReadOnlyList<MonitoringActivity> Snapshot(int limit, DateTimeOffset? since = null)
    {
        lock (_gate)
        {
            var query = _buffer.AsEnumerable();

            if (since.HasValue)
            {
                query = query.Where(a => a.Timestamp >= since.Value);
            }

            return query
                .Take(limit <= 0 ? 50 : limit)
                .ToList();
        }
    }
}
