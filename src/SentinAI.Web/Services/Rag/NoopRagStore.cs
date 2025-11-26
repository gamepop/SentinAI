using Microsoft.Extensions.Logging;

namespace SentinAI.Web.Services.Rag;

/// <summary>
/// Placeholder implementation used when the RAG integration is disabled.
/// </summary>
public class NoopRagStore : IRagStore
{
    private readonly ILogger<NoopRagStore> _logger;

    public NoopRagStore(ILogger<NoopRagStore> logger)
    {
        _logger = logger;
    }

    public bool IsEnabled => false;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("RAG store disabled; skipping initialization.");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RagMemory>> QueryAsync(
        string? sessionId,
        string query,
        int? limit,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<RagMemory>>(Array.Empty<RagMemory>());
    }

    public Task<IReadOnlyList<RagMemory>> GetAllRecentAsync(int limit, CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<RagMemory>>(Array.Empty<RagMemory>());
    }

    public Task StoreAsync(
        string sessionId,
        string content,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
