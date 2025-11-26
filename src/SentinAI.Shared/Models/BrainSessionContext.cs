namespace SentinAI.Shared.Models;

/// <summary>
/// Context for a brain analysis session
/// </summary>
public class BrainSessionContext
{
    /// <summary>
    /// Unique identifier for the session
    /// </summary>
    public string SessionId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Optional hint about what the user is looking for
    /// </summary>
    public string? QueryHint { get; set; }

    /// <summary>
    /// Additional metadata for the session
    /// </summary>
    public Dictionary<string, string>? Metadata { get; set; }

    public BrainSessionContext() { }

    public BrainSessionContext(string sessionId, string? queryHint, Dictionary<string, string>? metadata = null)
    {
        SessionId = sessionId;
        QueryHint = queryHint;
        Metadata = metadata;
    }

    /// <summary>
    /// Creates a new session context with a random session ID
    /// </summary>
    public static BrainSessionContext Create(string? queryHint = null)
    {
        return new BrainSessionContext
        {
            SessionId = Guid.NewGuid().ToString(),
            QueryHint = queryHint
        };
    }
}
