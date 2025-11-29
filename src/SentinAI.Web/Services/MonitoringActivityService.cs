using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SentinAI.Shared;
using SentinAI.Shared.Models;

namespace SentinAI.Web.Services;

public interface IMonitoringActivityService
{
    Task<IReadOnlyList<MonitoringActivity>> GetRecentAsync(int limit, DateTimeOffset? since, CancellationToken cancellationToken);
}

public class MonitoringActivityService : IMonitoringActivityService
{
    private readonly AgentService.AgentServiceClient? _agentClient;
    private readonly ILogger<MonitoringActivityService> _logger;

    public MonitoringActivityService(
        AgentService.AgentServiceClient? agentClient,
        ILogger<MonitoringActivityService> logger)
    {
        _agentClient = agentClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MonitoringActivity>> GetRecentAsync(int limit, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        if (_agentClient == null)
        {
            _logger.LogWarning("Sentinel gRPC client not available; returning empty activity set");
            return Array.Empty<MonitoringActivity>();
        }

        var request = new MonitoringActivityRequest
        {
            Limit = limit <= 0 ? 50 : limit
        };

        if (since.HasValue)
        {
            request.SinceUnixTimeMs = since.Value.ToUnixTimeMilliseconds();
        }

        try
        {
            var response = await _agentClient.GetMonitoringActivityAsync(request, cancellationToken: cancellationToken);
            return response.Items.Select(ToModel).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch monitoring activity from Sentinel");
            return Array.Empty<MonitoringActivity>();
        }
    }

    private static MonitoringActivity ToModel(MonitoringActivityItem grpcItem)
    {
        var metadata = grpcItem.Metadata.Count == 0
            ? null
            : grpcItem.Metadata.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        return new MonitoringActivity
        {
            Id = grpcItem.Id,
            Type = Enum.TryParse<MonitoringActivityType>(grpcItem.Type, ignoreCase: true, out var parsed)
                ? parsed
                : MonitoringActivityType.Custom,
            Scope = grpcItem.Scope,
            Drive = grpcItem.Drive,
            State = grpcItem.State,
            Message = grpcItem.Message,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(grpcItem.TimestampUnixTimeMs),
            Metadata = metadata
        };
    }
}
