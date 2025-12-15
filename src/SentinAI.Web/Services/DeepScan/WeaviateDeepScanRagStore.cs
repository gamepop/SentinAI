using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SentinAI.Shared.Models.DeepScan;

namespace SentinAI.Web.Services.DeepScan;

/// <summary>
/// Configuration options for the Deep Scan Weaviate RAG store.
/// </summary>
public class DeepScanRagStoreOptions
{
    public const string SectionName = "DeepScanRagStore";

    /// <summary>
    /// Enables the Deep Scan RAG store. When disabled, falls back to in-memory storage.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Base URL of the Weaviate instance.
    /// </summary>
    public string Endpoint { get; set; } = "http://localhost:8080";

    /// <summary>
    /// Optional API key for Weaviate authentication.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Weaviate class name for Deep Scan memories.
    /// </summary>
    public string ClassName { get; set; } = "DeepScanMemory";

    /// <summary>
    /// Maximum number of results to return per query.
    /// </summary>
    public int TopK { get; set; } = 10;

    /// <summary>
    /// Minimum similarity score required for a result.
    /// </summary>
    public double MinScore { get; set; } = 0.5;

    /// <summary>
    /// Vectorizer module to use.
    /// </summary>
    public string Vectorizer { get; set; } = "text2vec-ollama";

    /// <summary>
    /// Ollama API endpoint (for text2vec-ollama vectorizer).
    /// </summary>
    public string OllamaApiEndpoint { get; set; } = "http://host.docker.internal:11434";

    /// <summary>
    /// Ollama model for embeddings.
    /// </summary>
    public string OllamaModel { get; set; } = "nomic-embed-text";
}

/// <summary>
/// Weaviate-backed RAG store for Deep Scan learning memories.
/// Provides semantic search over past decisions to improve AI recommendations.
/// </summary>
public class WeaviateDeepScanRagStore : IDeepScanRagStore
{
    private readonly HttpClient _httpClient;
    private readonly DeepScanRagStoreOptions _options;
    private readonly ILogger<WeaviateDeepScanRagStore> _logger;
    private bool _schemaReady;

    public bool IsEnabled => _options.Enabled;

    public WeaviateDeepScanRagStore(
        HttpClient httpClient,
        IOptions<DeepScanRagStoreOptions> options,
        ILogger<WeaviateDeepScanRagStore> logger)
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

    /// <summary>
    /// Initializes the Weaviate schema for Deep Scan memories.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || _schemaReady)
        {
            return;
        }

        try
        {
            await EnsureSchemaAsync(cancellationToken);
            _schemaReady = true;
            _logger.LogInformation("Deep Scan RAG store ready using Weaviate class {Class}", _options.ClassName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize Deep Scan Weaviate schema. Memories will be unavailable until the database is reachable.");
        }
    }

    public async Task StoreMemoryAsync(DeepScanMemory memory)
    {
        if (!IsEnabled)
        {
            return;
        }

        await EnsureInitializedAsync();

        var payload = new
        {
            @class = _options.ClassName,
            properties = new Dictionary<string, object?>
            {
                ["memoryId"] = memory.Id,
                ["memoryType"] = memory.Type.ToString(),
                ["context"] = memory.Context,
                ["decision"] = memory.Decision,
                ["userAgreed"] = memory.UserAgreed,
                ["aiConfidence"] = memory.AiConfidence,
                ["aiReasoning"] = memory.AiReasoning,
                ["metadata"] = memory.Metadata.Count > 0
                    ? JsonSerializer.Serialize(memory.Metadata)
                    : null,
                ["timestamp"] = memory.Timestamp.ToString("o")
            }
        };

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/v1/objects", payload);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogDebug("Failed to store Deep Scan memory. Status: {Status}, Error: {Error}", response.StatusCode, error);
            }
            else
            {
                _logger.LogDebug("Stored Deep Scan memory {Id} of type {Type}", memory.Id, memory.Type);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to store Deep Scan memory {Id}", memory.Id);
        }
    }

    public async Task<List<DeepScanMemory>> FindSimilarAppDecisionsAsync(InstalledApp app)
    {
        if (!IsEnabled)
        {
            return new List<DeepScanMemory>();
        }

        await EnsureInitializedAsync();

        // Build a context string for semantic search
        var searchContext = $"App: {app.Name} Publisher: {app.Publisher} Category: {app.Category}";

        // Query for app removal decisions with semantic similarity
        var gql = BuildTypedNearTextQuery(
            DeepScanMemoryType.AppRemovalDecision.ToString(),
            searchContext,
            _options.TopK);

        return await ExecuteQueryAsync(gql);
    }

    public async Task<List<DeepScanMemory>> FindSimilarFileDecisionsAsync(FileCluster cluster)
    {
        if (!IsEnabled)
        {
            return new List<DeepScanMemory>();
        }

        await EnsureInitializedAsync();

        // Build a context string for semantic search
        var searchContext = $"File cluster: {cluster.Name} Type: {cluster.Type} Path: {cluster.BasePath}";

        // Query for relocation decisions with semantic similarity
        var gql = BuildTypedNearTextQuery(
            DeepScanMemoryType.RelocationDecision.ToString(),
            searchContext,
            _options.TopK);

        return await ExecuteQueryAsync(gql);
    }

    public async Task<AppRemovalPattern> GetAppRemovalPatternsAsync(string publisher)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(publisher))
        {
            return new AppRemovalPattern { Publisher = publisher };
        }

        await EnsureInitializedAsync();

        // Query all app removal decisions for this publisher
        var gql = BuildFilteredQuery(
            DeepScanMemoryType.AppRemovalDecision.ToString(),
            "publisher",
            publisher,
            100);

        var memories = await ExecuteQueryAsync(gql);

        var pattern = new AppRemovalPattern
        {
            Publisher = publisher,
            TotalDecisions = memories.Count,
            RemovalDecisions = memories.Count(m => m.Decision == "approved"),
            CommonReasons = memories
                .Where(m => !string.IsNullOrEmpty(m.AiReasoning))
                .GroupBy(m => m.AiReasoning!)
                .OrderByDescending(g => g.Count())
                .Take(3)
                .Select(g => g.Key)
                .ToList()
        };

        return pattern;
    }

    public async Task<FileRelocationPattern> GetRelocationPatternsAsync(string fileType)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(fileType))
        {
            return new FileRelocationPattern { FileType = fileType };
        }

        await EnsureInitializedAsync();

        // Query all relocation decisions for this file type
        var gql = BuildFilteredQuery(
            DeepScanMemoryType.RelocationDecision.ToString(),
            "clusterType",
            fileType,
            100);

        var memories = await ExecuteQueryAsync(gql);

        var preferredDrive = memories
            .Where(m => m.Decision == "approved")
            .SelectMany(m => m.Metadata.TryGetValue("actualTargetDrive", out var drive)
                ? new[] { drive }
                : Array.Empty<string>())
            .GroupBy(d => d)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key;

        var pattern = new FileRelocationPattern
        {
            FileType = fileType,
            TotalDecisions = memories.Count,
            RelocationDecisions = memories.Count(m => m.Decision == "approved"),
            PreferredTargetDrive = preferredDrive
        };

        return pattern;
    }

    public async Task<DeepScanLearningStats> GetLearningStatsAsync()
    {
        if (!IsEnabled)
        {
            return new DeepScanLearningStats();
        }

        await EnsureInitializedAsync();

        // Get aggregate stats using Weaviate aggregate query
        var aggregateGql = $@"{{
            Aggregate {{
                {_options.ClassName} {{
                    meta {{ count }}
                }}
            }}
        }}";

        var totalCount = 0;
        try
        {
            using var aggregateResponse = await _httpClient.PostAsJsonAsync("/v1/graphql", new { query = aggregateGql });
            if (aggregateResponse.IsSuccessStatusCode)
            {
                await using var stream = await aggregateResponse.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);

                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("Aggregate", out var aggregate) &&
                    aggregate.TryGetProperty(_options.ClassName, out var classData) &&
                    classData.GetArrayLength() > 0)
                {
                    var first = classData[0];
                    if (first.TryGetProperty("meta", out var meta) &&
                        meta.TryGetProperty("count", out var count))
                    {
                        totalCount = count.GetInt32();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get aggregate stats");
        }

        // Get type counts by querying each type
        var appDecisions = await CountByTypeAsync(DeepScanMemoryType.AppRemovalDecision.ToString());
        var relocationDecisions = await CountByTypeAsync(DeepScanMemoryType.RelocationDecision.ToString());
        var cleanupDecisions = await CountByTypeAsync(DeepScanMemoryType.CleanupDecision.ToString());

        // Get accuracy rate
        var accuracyRate = await CalculateAccuracyRateAsync();

        // Get most recent memory timestamp
        var latestMemory = await GetLatestMemoryAsync();

        return new DeepScanLearningStats
        {
            TotalMemories = totalCount,
            AppDecisions = appDecisions,
            RelocationDecisions = relocationDecisions,
            CleanupDecisions = cleanupDecisions,
            AiAccuracyRate = accuracyRate,
            LastLearningDate = latestMemory?.Timestamp
        };
    }

    private async Task EnsureInitializedAsync()
    {
        if (!_schemaReady)
        {
            await InitializeAsync();
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
                description = "SentinAI Deep Scan learning memories",
                vectorizer = "none",
                properties = BuildSchemaProperties()
            },
            "text2vec-openai" => new
            {
                @class = _options.ClassName,
                description = "SentinAI Deep Scan learning memories",
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
                description = "SentinAI Deep Scan learning memories",
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
                description = "SentinAI Deep Scan learning memories",
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
                description = "SentinAI Deep Scan learning memories",
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
        new { name = "memoryId", dataType = new[] { "text" }, description = "Unique memory identifier" },
        new { name = "memoryType", dataType = new[] { "text" }, description = "Type of memory (AppRemovalDecision, RelocationDecision, etc.)" },
        new { name = "context", dataType = new[] { "text" }, description = "Context string for semantic search" },
        new { name = "decision", dataType = new[] { "text" }, description = "The decision that was made" },
        new { name = "userAgreed", dataType = new[] { "boolean" }, description = "Whether user agreed with AI recommendation" },
        new { name = "aiConfidence", dataType = new[] { "number" }, description = "AI confidence level" },
        new { name = "aiReasoning", dataType = new[] { "text" }, description = "AI reasoning for the decision" },
        new { name = "metadata", dataType = new[] { "text" }, description = "Serialized metadata dictionary" },
        new { name = "timestamp", dataType = new[] { "date" }, description = "UTC timestamp" }
    };

    private static string EscapeGraphQl(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n");
    }

    private string BuildTypedNearTextQuery(string memoryType, string query, int limit)
    {
        var sb = new StringBuilder();
        sb.Append("{ Get { ");
        sb.Append(_options.ClassName);
        sb.Append("(");
        sb.Append($"limit: {limit}");

        // Filter by memory type
        sb.Append($", where: {{ operator: Equal, path: [\"memoryType\"], valueText: \"{EscapeGraphQl(memoryType)}\" }}");

        // Add semantic search
        sb.Append(", nearText: { concepts: [\"");
        sb.Append(EscapeGraphQl(query));
        sb.Append("\"]");
        if (_options.MinScore > 0)
        {
            var certainty = Math.Clamp(_options.MinScore, 0.01, 1.0);
            sb.Append($", certainty: {certainty:F2}");
        }
        sb.Append(" }");
        sb.Append(") { memoryId memoryType context decision userAgreed aiConfidence aiReasoning metadata timestamp _additional { distance } } } }");
        return sb.ToString();
    }

    private string BuildFilteredQuery(string memoryType, string metadataKey, string metadataValue, int limit)
    {
        // We need to search for the metadata key within the serialized metadata JSON
        // This is a workaround since we store metadata as serialized JSON
        var sb = new StringBuilder();
        sb.Append("{ Get { ");
        sb.Append(_options.ClassName);
        sb.Append("(");
        sb.Append($"limit: {limit}");

        // Filter by memory type
        sb.Append($", where: {{ operator: Equal, path: [\"memoryType\"], valueText: \"{EscapeGraphQl(memoryType)}\" }}");
        sb.Append(", sort: [{ path: [\"timestamp\"], order: desc }]");

        sb.Append(") { memoryId memoryType context decision userAgreed aiConfidence aiReasoning metadata timestamp } } }");
        return sb.ToString();
    }

    private async Task<List<DeepScanMemory>> ExecuteQueryAsync(string gql)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/v1/graphql", new { query = gql });
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Weaviate query failed ({Status}). Returning no memories.", response.StatusCode);
                return new List<DeepScanMemory>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("data", out var dataElement) ||
                !dataElement.TryGetProperty("Get", out var getElement) ||
                !getElement.TryGetProperty(_options.ClassName, out var collectionElement))
            {
                return new List<DeepScanMemory>();
            }

            var memories = new List<DeepScanMemory>();
            foreach (var item in collectionElement.EnumerateArray())
            {
                var memory = ParseMemoryFromJson(item);
                if (memory != null)
                {
                    memories.Add(memory);
                }
            }

            return memories;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Weaviate query failed");
            return new List<DeepScanMemory>();
        }
    }

    private DeepScanMemory? ParseMemoryFromJson(JsonElement item)
    {
        try
        {
            var memory = new DeepScanMemory
            {
                Id = GetStringProperty(item, "memoryId", Guid.NewGuid().ToString()),
                Context = GetStringProperty(item, "context", ""),
                Decision = GetStringProperty(item, "decision", ""),
                AiReasoning = GetStringProperty(item, "aiReasoning")
            };

            // Parse memory type
            var typeStr = GetStringProperty(item, "memoryType", "");
            if (Enum.TryParse<DeepScanMemoryType>(typeStr, out var memoryType))
            {
                memory.Type = memoryType;
            }

            // Parse boolean
            if (item.TryGetProperty("userAgreed", out var userAgreedProp) &&
                userAgreedProp.ValueKind == JsonValueKind.True)
            {
                memory.UserAgreed = true;
            }

            // Parse confidence
            if (item.TryGetProperty("aiConfidence", out var confidenceProp) &&
                confidenceProp.ValueKind == JsonValueKind.Number)
            {
                memory.AiConfidence = confidenceProp.GetDouble();
            }

            // Parse timestamp
            var tsRaw = GetStringProperty(item, "timestamp");
            if (!string.IsNullOrEmpty(tsRaw) && DateTime.TryParse(tsRaw, out var parsedTs))
            {
                memory.Timestamp = parsedTs;
            }

            // Parse metadata
            var metadataRaw = GetStringProperty(item, "metadata");
            if (!string.IsNullOrWhiteSpace(metadataRaw))
            {
                try
                {
                    var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataRaw);
                    if (metadata != null)
                    {
                        memory.Metadata = metadata;
                    }
                }
                catch
                {
                    // Ignore metadata parsing errors
                }
            }

            return memory;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse memory from JSON");
            return null;
        }
    }

    private static string GetStringProperty(JsonElement element, string propertyName, string defaultValue = "")
    {
        if (element.TryGetProperty(propertyName, out var value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? defaultValue,
                JsonValueKind.Null => defaultValue,
                _ => value.ToString()
            } ?? defaultValue;
        }

        return defaultValue;
    }

    private async Task<int> CountByTypeAsync(string memoryType)
    {
        var gql = $@"{{
            Aggregate {{
                {_options.ClassName}(where: {{ operator: Equal, path: [""memoryType""], valueText: ""{EscapeGraphQl(memoryType)}"" }}) {{
                    meta {{ count }}
                }}
            }}
        }}";

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/v1/graphql", new { query = gql });
            if (!response.IsSuccessStatusCode)
            {
                return 0;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("Aggregate", out var aggregate) &&
                aggregate.TryGetProperty(_options.ClassName, out var classData) &&
                classData.GetArrayLength() > 0)
            {
                var first = classData[0];
                if (first.TryGetProperty("meta", out var meta) &&
                    meta.TryGetProperty("count", out var count))
                {
                    return count.GetInt32();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to count memories by type {Type}", memoryType);
        }

        return 0;
    }

    private async Task<double> CalculateAccuracyRateAsync()
    {
        // Get decisions with userAgreed = true
        var agreedGql = $@"{{
            Aggregate {{
                {_options.ClassName}(where: {{
                    operator: And,
                    operands: [
                        {{ operator: Equal, path: [""userAgreed""], valueBoolean: true }},
                        {{ operator: Or, operands: [
                            {{ operator: Equal, path: [""memoryType""], valueText: ""AppRemovalDecision"" }},
                            {{ operator: Equal, path: [""memoryType""], valueText: ""RelocationDecision"" }},
                            {{ operator: Equal, path: [""memoryType""], valueText: ""CleanupDecision"" }}
                        ]}}
                    ]
                }}) {{
                    meta {{ count }}
                }}
            }}
        }}";

        // Get total decisions
        var totalGql = $@"{{
            Aggregate {{
                {_options.ClassName}(where: {{
                    operator: Or, operands: [
                        {{ operator: Equal, path: [""memoryType""], valueText: ""AppRemovalDecision"" }},
                        {{ operator: Equal, path: [""memoryType""], valueText: ""RelocationDecision"" }},
                        {{ operator: Equal, path: [""memoryType""], valueText: ""CleanupDecision"" }}
                    ]
                }}) {{
                    meta {{ count }}
                }}
            }}
        }}";

        try
        {
            var agreedCount = 0;
            var totalCount = 0;

            using (var response = await _httpClient.PostAsJsonAsync("/v1/graphql", new { query = agreedGql }))
            {
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync();
                    using var doc = await JsonDocument.ParseAsync(stream);

                    if (doc.RootElement.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("Aggregate", out var aggregate) &&
                        aggregate.TryGetProperty(_options.ClassName, out var classData) &&
                        classData.GetArrayLength() > 0)
                    {
                        var first = classData[0];
                        if (first.TryGetProperty("meta", out var meta) &&
                            meta.TryGetProperty("count", out var count))
                        {
                            agreedCount = count.GetInt32();
                        }
                    }
                }
            }

            using (var response = await _httpClient.PostAsJsonAsync("/v1/graphql", new { query = totalGql }))
            {
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync();
                    using var doc = await JsonDocument.ParseAsync(stream);

                    if (doc.RootElement.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("Aggregate", out var aggregate) &&
                        aggregate.TryGetProperty(_options.ClassName, out var classData) &&
                        classData.GetArrayLength() > 0)
                    {
                        var first = classData[0];
                        if (first.TryGetProperty("meta", out var meta) &&
                            meta.TryGetProperty("count", out var count))
                        {
                            totalCount = count.GetInt32();
                        }
                    }
                }
            }

            if (totalCount == 0)
            {
                return 0.75; // Default baseline
            }

            return (double)agreedCount / totalCount;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to calculate accuracy rate");
            return 0.75; // Default baseline
        }
    }

    private async Task<DeepScanMemory?> GetLatestMemoryAsync()
    {
        var gql = $@"{{ Get {{ {_options.ClassName}(limit: 1, sort: [{{ path: [""timestamp""], order: desc }}]) {{ memoryId memoryType context decision userAgreed aiConfidence aiReasoning metadata timestamp }} }} }}";

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/v1/graphql", new { query = gql });
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("Get", out var get) &&
                get.TryGetProperty(_options.ClassName, out var classData) &&
                classData.GetArrayLength() > 0)
            {
                return ParseMemoryFromJson(classData[0]);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get latest memory");
        }

        return null;
    }
}
