namespace SentinAI.Web.Services.Rag;

public interface IRagStore
{
    bool IsEnabled { get; }

    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Query memories with semantic search
    /// </summary>
    /// <param name="sessionId">Session ID to filter by, or null to search all sessions</param>
    /// <param name="query">Semantic search query</param>
    /// <param name="limit">Max results</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<IReadOnlyList<RagMemory>> QueryAsync(
        string? sessionId,
        string query,
        int? limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Get all recent memories regardless of session
    /// </summary>
    Task<IReadOnlyList<RagMemory>> GetAllRecentAsync(int limit, CancellationToken cancellationToken);

    Task StoreAsync(
        string sessionId,
        string content,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken);

    Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken);
}
