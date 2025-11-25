using SentinAI.Shared.Models;

namespace SentinAI.Shared.Services;

public interface IMonitoringActivityPublisher
{
    Task PublishAsync(MonitoringActivity activity, CancellationToken cancellationToken = default);
}
