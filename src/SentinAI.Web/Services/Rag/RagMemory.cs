namespace SentinAI.Web.Services.Rag;

public record RagMemory(
    string SessionId,
    string Content,
    DateTimeOffset Timestamp,
    double Score,
    IReadOnlyDictionary<string, string>? Metadata);
