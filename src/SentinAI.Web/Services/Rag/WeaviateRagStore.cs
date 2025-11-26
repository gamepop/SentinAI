using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SentinAI.Web.Services.Rag;

/// <summary>
/// Weaviate-backed RAG store that persists long-term "memories" for the AgentBrain.
/// Assumes a local Weaviate instance with the text2vec-transformers module enabled
/// so that it can vectorize content server-side without additional embedding code.
/// </summary>
public class WeaviateRagStore : IRagStore
{
    private readonly HttpClient _httpClient;
    private readonly RagStoreOptions _options;
    private readonly ILogger<WeaviateRagStore> _logger;
    private bool _schemaReady;

    public bool IsEnabled => _options.Enabled;

    public WeaviateRagStore(
        HttpClient httpClient,
        IOptions<RagStoreOptions> options,
        ILogger<WeaviateRagStore> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        if (_httpClient.BaseAddress == null && !string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            _httpClient.BaseAddress = new Uri(_options.Endpoint);
        }

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Remove("Authorization");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {_options.ApiKey}");
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled || _schemaReady)
        {
            return;
        }

        try
        {
            await EnsureSchemaAsync(cancellationToken);
            _schemaReady = true;
            _logger.LogInformation("RAG store ready using Weaviate class {Class}", _options.ClassName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize Weaviate schema. Memories will be unavailable until the database is reachable.");
        }
    }

    public async Task<IReadOnlyList<RagMemory>> QueryAsync(
        string? sessionId,
        string query,
        int? limit,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<RagMemory>();
        }

        var topK = limit ?? _options.TopK;
        var gql = BuildNearTextQuery(sessionId, query, topK);
        var request = new { query = gql };

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/v1/graphql", request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Weaviate query failed ({Status}). Returning no memories.", response.StatusCode);
                return Array.Empty<RagMemory>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (!doc.RootElement.TryGetProperty("data", out var dataElement) ||
                !dataElement.TryGetProperty("Get", out var getElement) ||
                !getElement.TryGetProperty(_options.ClassName, out var collectionElement))
            {
                return Array.Empty<RagMemory>();
            }

            var memories = new List<RagMemory>();
            foreach (var item in collectionElement.EnumerateArray())
            {
                var itemSessionId = item.GetPropertyOrDefault("sessionId", string.Empty);
                var content = item.GetPropertyOrDefault("content", string.Empty);
                var tsRaw = item.GetPropertyOrDefault("timestamp", DateTimeOffset.UtcNow.ToString("o"));
                var timestamp = DateTimeOffset.TryParse(tsRaw, out var parsedTs)
                    ? parsedTs
                    : DateTimeOffset.UtcNow;
                var score = 0.0;

                if (item.TryGetProperty("_additional", out var additional) &&
                    additional.TryGetProperty("distance", out var distanceProp))
                {
                    var distance = distanceProp.GetDouble();
                    score = 1.0 - distance;
                }

                if (score < _options.MinScore)
                {
                    continue;
                }

                var metadata = ParseMetadata(item.GetPropertyOrDefault("metadata"));
                memories.Add(new RagMemory(itemSessionId, content, timestamp, score, metadata));
            }

            return memories;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Weaviate query failed for session {SessionId}", sessionId);
            return Array.Empty<RagMemory>();
        }
    }

    public async Task<IReadOnlyList<RagMemory>> GetAllRecentAsync(int limit, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return Array.Empty<RagMemory>();
        }

        // Query all memories sorted by timestamp descending
        var gql = $"{{ Get {{ {_options.ClassName}(limit: {limit}, sort: [{{ path: [\"timestamp\"], order: desc }}]) {{ content sessionId metadata timestamp }} }} }}";
        var request = new { query = gql };

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/v1/graphql", request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Weaviate GetAllRecent failed ({Status}). Returning no memories.", response.StatusCode);
                return Array.Empty<RagMemory>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (!doc.RootElement.TryGetProperty("data", out var dataElement) ||
                !dataElement.TryGetProperty("Get", out var getElement) ||
                !getElement.TryGetProperty(_options.ClassName, out var collectionElement))
            {
                return Array.Empty<RagMemory>();
            }

            var memories = new List<RagMemory>();
            foreach (var item in collectionElement.EnumerateArray())
            {
                var sessionId = item.GetPropertyOrDefault("sessionId", string.Empty);
                var content = item.GetPropertyOrDefault("content", string.Empty);
                var tsRaw = item.GetPropertyOrDefault("timestamp", DateTimeOffset.UtcNow.ToString("o"));
                var timestamp = DateTimeOffset.TryParse(tsRaw, out var parsedTs)
                    ? parsedTs
                    : DateTimeOffset.UtcNow;

                var metadata = ParseMetadata(item.GetPropertyOrDefault("metadata"));
                memories.Add(new RagMemory(sessionId, content, timestamp, 1.0, metadata));
            }

            return memories;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Weaviate GetAllRecent failed");
            return Array.Empty<RagMemory>();
        }
    }

    public async Task StoreAsync(
        string sessionId,
        string content,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var payload = new
        {
            @class = _options.ClassName,
            properties = new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["content"] = content,
                ["metadata"] = metadata != null && metadata.Count > 0
                    ? JsonSerializer.Serialize(metadata)
                    : null,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("o")
            }
        };

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/v1/objects", payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Failed to persist memory. Status: {Status}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to store memory for session {SessionId}", sessionId);
        }
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var mutation = BuildDeleteMutation(sessionId);
        var request = new { query = mutation };

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/v1/graphql", request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to delete memories for session {SessionId}. Status: {Status}", sessionId, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error deleting session memories for {SessionId}", sessionId);
        }
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        using var checkResponse = await _httpClient.GetAsync($"/v1/schema/{_options.ClassName}", cancellationToken);
        if (checkResponse.IsSuccessStatusCode)
        {
            return;
        }

        if (checkResponse.StatusCode != HttpStatusCode.NotFound)
        {
            checkResponse.EnsureSuccessStatusCode();
        }

        // Build schema based on configured vectorizer
        var vectorizer = _options.Vectorizer?.ToLowerInvariant() ?? "none";

        object schema = vectorizer switch
        {
            "none" => new
            {
                @class = _options.ClassName,
                description = "SentinAI AgentBrain working memory",
                vectorizer = "none",
                properties = BuildSchemaProperties()
            },
            "text2vec-openai" => new
            {
                @class = _options.ClassName,
                description = "SentinAI AgentBrain working memory",
                vectorizer = "text2vec-openai",
                moduleConfig = new Dictionary<string, object>
                {
                    ["text2vec-openai"] = new Dictionary<string, object>
                    {
                        ["model"] = "text-embedding-3-small",
                        ["vectorizeClassName"] = false
                    }
                },
                properties = BuildSchemaProperties()
            },
            "text2vec-ollama" => new
            {
                @class = _options.ClassName,
                description = "SentinAI AgentBrain working memory",
                vectorizer = "text2vec-ollama",
                moduleConfig = new Dictionary<string, object>
                {
                    ["text2vec-ollama"] = new Dictionary<string, object>
                    {
                        ["apiEndpoint"] = _options.OllamaApiEndpoint,
                        ["model"] = _options.OllamaModel,
                        ["vectorizeClassName"] = false
                    }
                },
                properties = BuildSchemaProperties()
            },
            "text2vec-weaviate" => new
            {
                @class = _options.ClassName,
                description = "SentinAI AgentBrain working memory",
                vectorizer = "text2vec-weaviate",
                moduleConfig = new Dictionary<string, object>
                {
                    ["text2vec-weaviate"] = new Dictionary<string, object>
                    {
                        ["vectorizeClassName"] = false
                    }
                },
                properties = BuildSchemaProperties()
            },
            _ => new
            {
                @class = _options.ClassName,
                description = "SentinAI AgentBrain working memory",
                vectorizer = vectorizer,
                properties = BuildSchemaProperties()
            }
        };

        using var createResponse = await _httpClient.PostAsJsonAsync("/v1/schema", schema, cancellationToken);

        if (!createResponse.IsSuccessStatusCode)
        {
            var errorContent = await createResponse.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Weaviate schema creation failed ({Status}): {Error}", createResponse.StatusCode, errorContent);
            throw new HttpRequestException($"Schema creation failed: {createResponse.StatusCode} - {errorContent}");
        }
    }

    private static object[] BuildSchemaProperties() => new object[]
    {
        new { name = "sessionId", dataType = new[] { "text" }, description = "Analysis session identifier" },
        new { name = "content", dataType = new[] { "text" }, description = "Captured memory content" },
        new { name = "metadata", dataType = new[] { "text" }, description = "Serialized metadata" },
        new { name = "timestamp", dataType = new[] { "date" }, description = "UTC timestamp" }
    };

    private static string EscapeGraphQl(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n");
    }

    private string BuildNearTextQuery(string? sessionId, string query, int limit)
    {
        var sb = new StringBuilder();
        sb.Append("{ Get { ");
        sb.Append(_options.ClassName);
        sb.Append("(");
        sb.Append($"limit: {limit}");

        // Only filter by sessionId if one is provided (not null or empty)
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            sb.Append($", where: {{ operator: Equal, path: [\"sessionId\"], valueText: \"{EscapeGraphQl(sessionId)}\" }}");
        }

        sb.Append(", nearText: { concepts: [\"");
        sb.Append(EscapeGraphQl(query));
        sb.Append("\"]");
        if (_options.MinScore > 0)
        {
            // Convert min score to certainty (0-1) expected by Weaviate
            var certainty = Math.Clamp(_options.MinScore, 0.01, 1.0);
            sb.Append($", certainty: {certainty:F2}");
        }
        sb.Append(" }");
        sb.Append(") { content sessionId metadata timestamp _additional { distance } } } }");
        return sb.ToString();
    }

    private string BuildDeleteMutation(string sessionId)
    {
        var where = $"{{ operator: Equal, path: [\"sessionId\"], valueText: \"{EscapeGraphQl(sessionId)}\" }}";
        return $"mutation {{ Delete {{ {_options.ClassName}(where: {where}) {{ success }} }} }}";
    }

    private static IReadOnlyDictionary<string, string>? ParseMetadata(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
        }
        catch
        {
            return null;
        }
    }
}

internal static class JsonExtensions
{
    public static string GetPropertyOrDefault(this JsonElement element, string propertyName, string defaultValue = "")
    {
        if (element.TryGetProperty(propertyName, out var value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? defaultValue,
                _ => value.ToString()
            } ?? defaultValue;
        }

        return defaultValue;
    }
}
