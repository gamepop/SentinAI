using System.ComponentModel.DataAnnotations;

namespace SentinAI.Web.Services.Rag;

/// <summary>
/// Configuration for the local RAG (Retrieval Augmented Generation) store.
/// Default implementation targets a local Weaviate instance running with
/// the text2vec-transformers module so embeddings are handled server-side.
/// </summary>
public class RagStoreOptions
{
    public const string SectionName = "RagStore";

    /// <summary>
    /// Enables the RAG store integration. When disabled the brain falls back to
    /// stateless heuristics/LLM analysis.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Provider name (currently only "Weaviate" is implemented but the interface
    /// allows for future backends such as Chroma).
    /// </summary>
    [Required]
    public string Provider { get; set; } = "Weaviate";

    /// <summary>
    /// Base URL of the vector database (e.g. http://localhost:8080).
    /// </summary>
    [Required]
    public string Endpoint { get; set; } = "http://localhost:8080";

    /// <summary>
    /// Optional API key/ bearer token required by the vector database.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Collection/Class name used for storing brain memories.
    /// </summary>
    [Required]
    public string ClassName { get; set; } = "BrainMemory";

    /// <summary>
    /// Maximum number of memories to retrieve per query.
    /// </summary>
    public int TopK { get; set; } = 5;

    /// <summary>
    /// Minimum similarity score (1 - distance) required for a memory to be
    /// included in the prompt. This keeps noisy matches out of the context window.
    /// </summary>
    public double MinScore { get; set; } = 0.5;

    /// <summary>
    /// Vectorizer module to use. Supported: "text2vec-ollama", "text2vec-openai", "text2vec-weaviate", "none".
    /// If "none", Weaviate won't auto-vectorize and you'd need to provide vectors manually.
    /// </summary>
    public string Vectorizer { get; set; } = "text2vec-ollama";

    /// <summary>
    /// Ollama API base URL (only used when Vectorizer is "text2vec-ollama").
    /// </summary>
    public string OllamaApiEndpoint { get; set; } = "http://host.docker.internal:11434";

    /// <summary>
    /// Ollama embedding model name (only used when Vectorizer is "text2vec-ollama").
    /// </summary>
    public string OllamaModel { get; set; } = "nomic-embed-text";
}
