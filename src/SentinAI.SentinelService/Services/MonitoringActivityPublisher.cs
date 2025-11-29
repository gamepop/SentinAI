using Microsoft.Extensions.Logging;
using SentinAI.Shared.Models;
using SentinAI.Shared.Services;

namespace SentinAI.SentinelService.Services;

public class MonitoringActivityPublisher : IMonitoringActivityPublisher
{
    private readonly ILogger<MonitoringActivityPublisher> _logger;
    private readonly IMonitoringActivityStore _store;

    public MonitoringActivityPublisher(
        ILogger<MonitoringActivityPublisher> logger,
        IMonitoringActivityStore store)
    {
        _logger = logger;
        _store = store;
    }

    public Task PublishAsync(MonitoringActivity activity, CancellationToken cancellationToken = default)
    {
        _store.Add(activity);
        
        // Log at Info level for visibility, with metadata details for model interactions
        if (activity.Type == MonitoringActivityType.ModelInteraction || activity.Type == MonitoringActivityType.BrainConnection)
        {
            var metadataStr = activity.Metadata != null 
                ? string.Join(", ", activity.Metadata.Select(kv => $"{kv.Key}={kv.Value}"))
                : string.Empty;
            _logger.LogInformation(
                "🧠 [{Type}] {Scope}: {Message} | {Metadata}",
                activity.Type, 
                activity.Scope, 
                activity.Message,
                metadataStr);
        }
        else
        {
            _logger.LogInformation(
                "📊 [{Type}] {Scope}: {Message}",
                activity.Type,
                activity.Scope,
                activity.Message);
        }
        
        return Task.CompletedTask;
    }
}
