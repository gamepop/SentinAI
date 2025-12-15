using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntimeGenAI;
using SentinAI.Shared.Models;
using SentinAI.Shared.Models.DeepScan;
using SentinAI.Web.Services.Rag;

namespace SentinAI.Web.Services;

public interface IAgentBrain
{
    Task<bool> InitializeAsync(string modelPath);
    Task<CleanupSuggestion> AnalyzeFolderAsync(
        string folderPath,
        List<string> fileNames,
        BrainSessionContext? sessionContext = null,
        CancellationToken cancellationToken = default);

    Task<List<CleanupSuggestion>> AnalyzeFilesAsync(
        List<string> filePaths,
        BrainSessionContext? sessionContext = null,
        CancellationToken cancellationToken = default);
    bool IsReady { get; }
    bool IsModelLoaded { get; }
    string ExecutionProvider { get; }

    /// <summary>
    /// Gets statistics about brain usage
    /// </summary>
    (int TotalAnalyses, int ModelDecisions, int HeuristicOnly, int SafeToDeleteCount) GetStats();

    /// <summary>
    /// Analyzes an app for removal using AI with RAG context.
    /// </summary>
    Task<DeepScanAiDecision> AnalyzeAppRemovalAsync(
        InstalledApp app,
        List<DeepScanMemory> similarDecisions,
        AppRemovalPattern publisherPattern,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes files for relocation using AI with RAG context.
    /// </summary>
    Task<DeepScanAiDecision> AnalyzeRelocationAsync(
        FileCluster cluster,
        List<DeepScanMemory> similarDecisions,
        FileRelocationPattern fileTypePattern,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Hybrid AI Brain: Heuristics provide context, Phi-4 Mini model makes final decisions
/// 
/// Flow:
/// 1. Heuristic analysis runs first (fast, rule-based)
/// 2. Heuristic results become context for AI model
/// 3. AI model (Phi-4 Mini) makes final SafeToDelete decision
/// 4. Falls back to heuristics-only if model unavailable
/// </summary>
public class AgentBrain : IAgentBrain, IDisposable
{
    private readonly ILogger<AgentBrain> _logger;
    private readonly BrainConfiguration _config;
    private readonly IRagStore _ragStore;
    private readonly SemaphoreSlim _modelLock = new(1, 1);

    private Model? _model;
    private Tokenizer? _tokenizer;
    private bool _isInitialized;
    private bool _isModelLoaded;
    private string? _modelPath;

    // Statistics
    private int _totalAnalyses;
    private int _modelDecisions;
    private int _heuristicOnly;
    private int _safeToDeleteCount;

    public bool IsReady => _isInitialized;
    public bool IsModelLoaded => _isModelLoaded;
    public string ExecutionProvider => _config.GetProviderDisplayName();

    public AgentBrain(
        ILogger<AgentBrain> logger,
        IOptions<BrainConfiguration> config,
        IRagStore ragStore)
    {
        _logger = logger;
        _config = config.Value;
        _ragStore = ragStore;
    }

    public (int TotalAnalyses, int ModelDecisions, int HeuristicOnly, int SafeToDeleteCount) GetStats()
        => (_totalAnalyses, _modelDecisions, _heuristicOnly, _safeToDeleteCount);

    public async Task<bool> InitializeAsync(string modelPath)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("🧠 Initializing Hybrid AgentBrain");
        _logger.LogInformation("   • Model path: {ModelPath}", modelPath);
        _logger.LogInformation("   • Execution Provider: {Provider}", _config.GetProviderDisplayName());
        _logger.LogInformation("   • Inference Timeout: {Timeout}s", _config.InferenceTimeoutSeconds);
        _logger.LogInformation("   • Max Sequence Length: {MaxSeq} tokens", _config.MaxSequenceLength);
        _logger.LogInformation("   • Max Output Tokens: {MaxOut} tokens", _config.MaxOutputTokens);

        try
        {
            _modelPath = modelPath;

            if (!Directory.Exists(modelPath))
            {
                _logger.LogWarning("⚠️ Model path does not exist: {ModelPath}. Using heuristic-only mode.", modelPath);
                _isInitialized = true;
                _isModelLoaded = false;
                return await Task.FromResult(true);
            }

            // Check for the provider-specific ONNX file (genai_config.json references the actual filename)
            var (onnxFileName, _) = _config.GetModelFileNames();
            var onnxFile = Path.Combine(modelPath, onnxFileName);
            if (!File.Exists(onnxFile))
            {
                _logger.LogWarning("⚠️ {OnnxFile} not found at {Path}. Using heuristic-only mode.", onnxFileName, onnxFile);
                _isInitialized = true;
                _isModelLoaded = false;
                return await Task.FromResult(true);
            }

            // Load the ONNX model
            _logger.LogInformation("📦 Loading Phi-4 Mini ONNX model ({Provider})...", _config.GetProviderDisplayName());
            _logger.LogInformation("📂 Model files: {Files}", string.Join(", ", Directory.GetFiles(modelPath).Select(Path.GetFileName)));

            await _modelLock.WaitAsync();
            try
            {
                var modelLoadSw = Stopwatch.StartNew();

                _logger.LogInformation("🔄 Creating ONNX Model instance...");
                _model = new Model(modelPath);
                _logger.LogInformation("✅ Model created in {Duration}ms", modelLoadSw.ElapsedMilliseconds);

                _logger.LogInformation("🔄 Creating Tokenizer...");
                _tokenizer = new Tokenizer(_model);
                _logger.LogInformation("✅ Tokenizer created in {Duration}ms", modelLoadSw.ElapsedMilliseconds);

                _isModelLoaded = true;
                _logger.LogInformation("✅ Phi-4 Mini model loaded successfully! AI decisions enabled. Total load time: {Duration}ms", modelLoadSw.ElapsedMilliseconds);
            }
            catch (Exception modelEx)
            {
                _logger.LogError(modelEx, "❌ Model/Tokenizer creation failed: {Message}", modelEx.Message);
                _isModelLoaded = false;
                _model?.Dispose();
                _model = null;
                _tokenizer?.Dispose();
                _tokenizer = null;
                // Don't rethrow - we can still use heuristics
            }
            finally
            {
                _modelLock.Release();
            }

            _isInitialized = true;

            sw.Stop();
            _logger.LogInformation("✅ AgentBrain initialized in {Duration}ms (AI mode: {AiMode})",
                sw.ElapsedMilliseconds, _isModelLoaded);

            return await Task.FromResult(true);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "❌ Failed to load Phi-4 Mini model. Falling back to heuristic-only mode.");
            _isInitialized = true;
            _isModelLoaded = false;
            return await Task.FromResult(true); // Still usable in heuristic mode
        }
    }

    public async Task<CleanupSuggestion> AnalyzeFolderAsync(
        string folderPath,
        List<string> fileNames,
        BrainSessionContext? sessionContext = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _totalAnalyses++;
        var sw = Stopwatch.StartNew();

        _logger.LogInformation("🔍 [Step 1/2] Running heuristic pre-analysis for: {Folder} ({FileCount} files)",
            folderPath, fileNames.Count);

        // ═══════════════════════════════════════════════════════════════════
        // STEP 1: Heuristic Analysis (fast, provides context for AI)
        // ═══════════════════════════════════════════════════════════════════
        var heuristicResult = RunHeuristicAnalysis(folderPath, fileNames);
        var contextualMemories = await RetrieveMemoriesAsync(
            sessionContext,
            folderPath,
            fileNames,
            heuristicResult,
            cancellationToken);

        _logger.LogInformation("📊 Heuristic pre-analysis: Category={Category}, Suggested={Safe}, Confidence={Confidence:P0}",
            heuristicResult.Category, heuristicResult.SafeToDelete, heuristicResult.Confidence);

        // ═══════════════════════════════════════════════════════════════════
        // STEP 2: AI Model Makes Final Decision (using heuristic as context)
        // ═══════════════════════════════════════════════════════════════════
        CleanupSuggestion finalResult;

        if (_isModelLoaded && _model != null && _tokenizer != null)
        {
            _logger.LogInformation("🤖 [Step 2/2] Phi-4 Mini AI making final decision (heuristics as context)...");

            try
            {
                _logger.LogInformation("🔄 Starting AI inference for {Folder}...", folderPath);
                finalResult = await GetModelDecisionAsync(
                    folderPath,
                    fileNames,
                    heuristicResult,
                    sessionContext,
                    contextualMemories,
                    cancellationToken);
                _modelDecisions++;

                var agreement = finalResult.SafeToDelete == heuristicResult.SafeToDelete ? "✅ AGREES" : "⚠️ OVERRIDES";
                _logger.LogInformation("🎯 AI Decision {Agreement}: SafeToDelete={Safe}, Confidence={Confidence:P0}",
                    agreement, finalResult.SafeToDelete, finalResult.Confidence);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ AI INFERENCE FAILED: {Message} | Stack: {Stack}",
                    ex.Message, ex.StackTrace?.Split('\n').FirstOrDefault());
                finalResult = heuristicResult;
                finalResult.Reason = $"[Heuristic-Fallback: {ex.Message}] {heuristicResult.Reason}";
                _heuristicOnly++;
            }
        }
        else
        {
            _logger.LogDebug("ℹ️ AI model not loaded, using heuristic result only");
            finalResult = heuristicResult;
            finalResult.Reason = $"[Heuristic] {heuristicResult.Reason}";
            _heuristicOnly++;
        }

        if (finalResult.SafeToDelete)
        {
            _safeToDeleteCount++;
        }

        await PersistMemoryAsync(
            sessionContext,
            folderPath,
            fileNames,
            heuristicResult,
            finalResult,
            cancellationToken);

        sw.Stop();
        _logger.LogInformation(
            "✅ Analysis complete in {Duration}ms: SafeToDelete={Safe}, DecisionBy={DecisionBy}",
            sw.ElapsedMilliseconds,
            finalResult.SafeToDelete,
            _isModelLoaded ? "AI+Heuristics" : "Heuristics-only");

        return finalResult;
    }

    /// <summary>
    /// Step 1: Fast heuristic analysis to provide context for the AI model
    /// </summary>
    private CleanupSuggestion RunHeuristicAnalysis(string folderPath, List<string> fileNames)
    {
        var suggestion = new CleanupSuggestion
        {
            FilePath = folderPath,
            SizeBytes = 0,
            SafeToDelete = false,
            Reason = "Unknown - requires analysis",
            Category = CleanupCategories.Unknown,
            Confidence = 0.0,
            AutoApprove = false
        };

        var lowerPath = folderPath.ToLowerInvariant();

        // Path-based detection
        var isWindowsTemp = lowerPath.Contains("windows\\temp");
        var isUserTemp = lowerPath.Contains("appdata\\local\\temp");
        var isBrowserCache = (lowerPath.Contains("cache") || lowerPath.Contains("cache2")) &&
                             (lowerPath.Contains("chrome") || lowerPath.Contains("edge") ||
                              lowerPath.Contains("firefox") || lowerPath.Contains("brave") ||
                              lowerPath.Contains("opera"));
        var isNodeModules = lowerPath.Contains("node_modules");
        var isBinObj = lowerPath.Contains("\\bin\\") || lowerPath.Contains("\\obj\\");
        var isDownloads = lowerPath.Contains("downloads");
        var isGenericTemp = lowerPath.Contains("temp") && !isWindowsTemp && !isUserTemp;
        var isGenericCache = lowerPath.Contains("cache") && !isBrowserCache;

        // File-based detection
        var hasTmpFiles = fileNames.Any(f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        var hasLogFiles = fileNames.Any(f => f.EndsWith(".log", StringComparison.OrdinalIgnoreCase));
        var hasCacheFiles = fileNames.Any(f =>
            f.EndsWith(".cache", StringComparison.OrdinalIgnoreCase) ||
            f.StartsWith("cache", StringComparison.OrdinalIgnoreCase));
        var hasImportantFiles = fileNames.Any(f =>
            f.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        // ═══ RULE ENGINE ═══

        // HIGH CONFIDENCE: System temp folders
        if (isWindowsTemp || isUserTemp)
        {
            suggestion.Category = CleanupCategories.Temp;
            suggestion.SafeToDelete = true;
            suggestion.Reason = isWindowsTemp
                ? "Windows system temp folder - safe to clean"
                : "User temp folder - safe to clean";
            suggestion.Confidence = 0.95;
            suggestion.AutoApprove = true;
        }
        // HIGH CONFIDENCE: Browser caches
        else if (isBrowserCache)
        {
            suggestion.Category = CleanupCategories.Cache;
            suggestion.SafeToDelete = true;
            suggestion.Reason = "Browser cache - will be regenerated automatically";
            suggestion.Confidence = 0.93;
            suggestion.AutoApprove = true;
        }
        // MEDIUM-HIGH: Node modules
        else if (isNodeModules)
        {
            suggestion.Category = CleanupCategories.NodeModules;
            suggestion.SafeToDelete = true;
            suggestion.Reason = "node_modules - regenerate with npm/yarn install";
            suggestion.Confidence = 0.85;
            suggestion.AutoApprove = false; // Developer should confirm
        }
        // MEDIUM: Build artifacts
        else if (isBinObj)
        {
            suggestion.Category = CleanupCategories.BuildArtifacts;
            suggestion.SafeToDelete = true;
            suggestion.Reason = "Build output - regenerate with dotnet build";
            suggestion.Confidence = 0.80;
            suggestion.AutoApprove = false;
        }
        // MEDIUM: Generic temp/cache
        else if (isGenericTemp || hasTmpFiles)
        {
            suggestion.Category = CleanupCategories.Temp;
            suggestion.SafeToDelete = true;
            suggestion.Reason = "Temporary files detected";
            suggestion.Confidence = 0.70;
            suggestion.AutoApprove = false;
        }
        else if (isGenericCache || hasCacheFiles)
        {
            suggestion.Category = CleanupCategories.Cache;
            suggestion.SafeToDelete = true;
            suggestion.Reason = "Cache files detected";
            suggestion.Confidence = 0.65;
            suggestion.AutoApprove = false;
        }
        // MEDIUM-LOW: Log files
        else if (hasLogFiles)
        {
            suggestion.Category = CleanupCategories.Logs;
            suggestion.SafeToDelete = true;
            suggestion.Reason = "Log files - usually safe to delete";
            suggestion.Confidence = 0.60;
            suggestion.AutoApprove = false;
        }
        // LOW CONFIDENCE: Downloads with user files
        else if (isDownloads)
        {
            suggestion.Category = CleanupCategories.Downloads;
            if (hasImportantFiles)
            {
                suggestion.SafeToDelete = false;
                suggestion.Reason = "Downloads folder contains documents/media - REVIEW CAREFULLY";
                suggestion.Confidence = 0.20;
            }
            else
            {
                suggestion.SafeToDelete = false; // Let AI decide
                suggestion.Reason = "Downloads folder - may contain important files";
                suggestion.Confidence = 0.40;
            }
            suggestion.AutoApprove = false;
        }
        // UNKNOWN: No matching rules
        else
        {
            suggestion.Category = CleanupCategories.Unknown;
            suggestion.SafeToDelete = false;
            suggestion.Reason = "Unknown folder type - AI analysis required";
            suggestion.Confidence = 0.10;
            suggestion.AutoApprove = false;
        }

        return suggestion;
    }

    private async Task<IReadOnlyList<RagMemory>> RetrieveMemoriesAsync(
        BrainSessionContext? sessionContext,
        string folderPath,
        List<string> fileNames,
        CleanupSuggestion heuristicResult,
        CancellationToken cancellationToken)
    {
        if (sessionContext == null || !_ragStore.IsEnabled)
        {
            return Array.Empty<RagMemory>();
        }

        var query = BuildMemoryQuery(sessionContext, folderPath, fileNames, heuristicResult);
        var memories = await _ragStore.QueryAsync(sessionContext.SessionId, query, null, cancellationToken);

        if (memories.Count > 0)
        {
            _logger.LogInformation("🧠 Retrieved {Count} memories for session {SessionId}", memories.Count, sessionContext.SessionId);
        }

        return memories;
    }

    private async Task PersistMemoryAsync(
        BrainSessionContext? sessionContext,
        string folderPath,
        List<string> fileNames,
        CleanupSuggestion heuristicResult,
        CleanupSuggestion finalResult,
        CancellationToken cancellationToken)
    {
        if (sessionContext == null || !_ragStore.IsEnabled)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Folder: {folderPath}");
        builder.AppendLine($"Decision: {(finalResult.SafeToDelete ? "SAFE" : "UNSAFE")} (confidence {finalResult.Confidence:F2})");
        builder.AppendLine($"Reason: {finalResult.Reason ?? "n/a"}");
        builder.AppendLine($"Heuristic: {heuristicResult.Category} safe={heuristicResult.SafeToDelete} conf={heuristicResult.Confidence:F2}");

        var sampleFiles = string.Join(", ", fileNames.Take(5));
        if (fileNames.Count > 5)
        {
            sampleFiles += $" (+{fileNames.Count - 5} more)";
        }
        builder.AppendLine($"Files: {sampleFiles}");

        var metadata = new Dictionary<string, string>
        {
            ["folderPath"] = folderPath,
            ["category"] = finalResult.Category ?? CleanupCategories.Unknown,
            ["safeToDelete"] = finalResult.SafeToDelete.ToString(),
            ["source"] = _isModelLoaded ? "AI" : "Heuristic"
        };

        if (!string.IsNullOrWhiteSpace(sessionContext.QueryHint))
        {
            metadata["queryHint"] = sessionContext.QueryHint!;
        }

        await _ragStore.StoreAsync(sessionContext.SessionId, builder.ToString(), metadata, cancellationToken);
    }

    private static string BuildMemoryQuery(
        BrainSessionContext sessionContext,
        string folderPath,
        List<string> fileNames,
        CleanupSuggestion heuristicResult)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(sessionContext.QueryHint))
        {
            builder.AppendLine(sessionContext.QueryHint);
        }
        builder.AppendLine(folderPath);
        builder.AppendLine($"Category:{heuristicResult.Category} Safe:{heuristicResult.SafeToDelete}");
        builder.AppendLine($"Files:{string.Join(", ", fileNames.Take(5))}");
        return builder.ToString();
    }

    /// <summary>
    /// Step 2: AI model makes the final decision using heuristic context
    /// </summary>
    private async Task<CleanupSuggestion> GetModelDecisionAsync(
        string folderPath,
        List<string> fileNames,
        CleanupSuggestion heuristicResult,
        BrainSessionContext? sessionContext,
        IReadOnlyList<RagMemory> contextualMemories,
        CancellationToken cancellationToken)
    {
        if (_model == null || _tokenizer == null)
        {
            return heuristicResult;
        }

        await _modelLock.WaitAsync(cancellationToken);
        try
        {
            // Build prompt with heuristic context
            var memories = contextualMemories ?? Array.Empty<RagMemory>();
            if (memories.Count > 0)
            {
                _logger.LogInformation("📚 Injecting {Count} contextual memories into prompt for session {Session}",
                    memories.Count,
                    sessionContext?.SessionId ?? "n/a");
            }

            var prompt = BuildPrompt(folderPath, fileNames, heuristicResult, sessionContext, memories);

            _logger.LogDebug("📝 AI Prompt ({Length} chars)", prompt.Length);

            // Tokenize
            var sequences = _tokenizer.Encode(prompt);
            var inputLength = sequences.NumSequences > 0 ? sequences[0].Length : 0;

            // Validate input length - must leave room for output tokens
            var maxInputLength = _config.MaxSequenceLength - _config.MaxOutputTokens;
            if (inputLength >= maxInputLength)
            {
                _logger.LogWarning("⚠️ Input too long ({InputLen} tokens) for max sequence ({MaxSeq}). Truncating prompt.",
                    inputLength, _config.MaxSequenceLength);

                // Fall back to heuristics for very long inputs
                throw new InvalidOperationException($"input sequence_length ({inputLength}) is >= max_length ({maxInputLength})");
            }

            _logger.LogInformation("📝 Tokenized: {InputLen} input tokens, max output: {MaxOut} tokens",
                inputLength, _config.MaxOutputTokens);

            // Generate with settings from config
            using var generatorParams = new GeneratorParams(_model);
            generatorParams.SetSearchOption("max_length", _config.MaxSequenceLength);
            generatorParams.SetSearchOption("temperature", _config.Temperature);
            generatorParams.SetSearchOption("top_p", 0.9);
            generatorParams.SetInputSequences(sequences);

            var outputTokens = new List<int>();
            var timeoutMs = _config.InferenceTimeoutSeconds * 1000;

            _logger.LogInformation("🤖 Starting AI inference (timeout: {Timeout}s, provider: {Provider})...",
                _config.InferenceTimeoutSeconds, _config.GetProviderDisplayName());

            using var generator = new Generator(_model, generatorParams);

            var sw = Stopwatch.StartNew();
            while (!generator.IsDone())
            {
                generator.ComputeLogits();
                generator.GenerateNextToken();

                var seq = generator.GetSequence(0);
                if (seq.Length > 0)
                {
                    outputTokens.Add(seq[^1]);
                }

                // Safety timeout from config
                if (sw.ElapsedMilliseconds > timeoutMs)
                {
                    _logger.LogWarning("⚠️ AI generation timeout after {Timeout}s", _config.InferenceTimeoutSeconds);
                    break;
                }
            }
            sw.Stop();

            // Decode
            var output = _tokenizer.Decode(outputTokens.ToArray());
            _logger.LogInformation("🤖 AI response ({Duration}ms, {TokenCount} tokens): {Output}",
                sw.ElapsedMilliseconds,
                outputTokens.Count,
                output.Length > 200 ? output[..200] + "..." : output);

            // Parse AI decision
            return ParseModelOutput(output, folderPath, heuristicResult);
        }
        finally
        {
            _modelLock.Release();
        }
    }

    private static string BuildPrompt(
        string folderPath,
        List<string> fileNames,
        CleanupSuggestion heuristic,
        BrainSessionContext? sessionContext,
        IReadOnlyList<RagMemory> memories)
    {
        // Limit files to reduce token count - only show 10 most relevant
        var sampleFiles = string.Join(", ", fileNames.Take(10));
        if (fileNames.Count > 10)
        {
            sampleFiles += $" (+{fileNames.Count - 10} more)";
        }

        // Compact prompt to minimize tokens while preserving context
        var prompt = new StringBuilder();
        prompt.AppendLine("<|system|>");
        prompt.AppendLine("You are a Windows cleanup safety analyzer. Decide if a folder is safe to delete.");
        prompt.AppendLine("Rules: Never delete user documents/photos. Temp/cache folders are safe. When in doubt, say no.");
        prompt.AppendLine("Respond ONLY with JSON: {\"safe_to_delete\":true/false,\"confidence\":0.0-1.0,\"reason\":\"brief reason\"}");
        prompt.AppendLine("<|end|>");
        prompt.AppendLine("<|user|>");
        prompt.AppendLine($"Path: {folderPath}");
        prompt.AppendLine($"Files ({fileNames.Count}): {sampleFiles}");
        prompt.AppendLine($"Heuristic: {heuristic.Category}, safe={heuristic.SafeToDelete}, conf={heuristic.Confidence:F2}");
        prompt.AppendLine($"Heuristic reason: {heuristic.Reason}");
        if (sessionContext != null)
        {
            prompt.AppendLine($"Session intent: {sessionContext.QueryHint ?? "unspecified"}");
        }
        if (memories.Count > 0)
        {
            prompt.AppendLine("Previous relevant analyses:");
            var idx = 1;
            foreach (var memory in memories.Take(3))
            {
                var summary = memory.Content.ReplaceLineEndings(" ");
                if (summary.Length > 250)
                {
                    summary = summary[..250] + "...";
                }
                prompt.AppendLine($"Memory {idx++}: {summary}");
            }
        }
        prompt.AppendLine("Your JSON decision:");
        prompt.AppendLine("<|end|>");
        prompt.AppendLine("<|assistant|>");

        return prompt.ToString();
    }

    private CleanupSuggestion ParseModelOutput(string output, string folderPath, CleanupSuggestion heuristic)
    {
        try
        {
            // Extract JSON from output
            var jsonStart = output.IndexOf('{');
            var jsonEnd = output.LastIndexOf('}');

            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonStr = output.Substring(jsonStart, jsonEnd - jsonStart + 1);

                using var doc = JsonDocument.Parse(jsonStr);
                var root = doc.RootElement;

                var safeToDelete = root.TryGetProperty("safe_to_delete", out var safeProp) && safeProp.GetBoolean();
                var confidence = root.TryGetProperty("confidence", out var confProp) ? confProp.GetDouble() : 0.5;
                var reason = root.TryGetProperty("reason", out var reasonProp) ? reasonProp.GetString() ?? "" : "";

                _logger.LogInformation("🎯 AI parsed decision: safe={Safe}, confidence={Conf:P0}", safeToDelete, confidence);

                return new CleanupSuggestion
                {
                    FilePath = folderPath,
                    SizeBytes = heuristic.SizeBytes,
                    Category = heuristic.Category,
                    SafeToDelete = safeToDelete,
                    Confidence = confidence,
                    Reason = $"[AI] {reason}",
                    AutoApprove = safeToDelete && confidence >= 0.90
                };
            }

            _logger.LogWarning("⚠️ No valid JSON found in AI output");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Failed to parse AI JSON: {Output}", output.Length > 100 ? output[..100] : output);
        }

        // Fallback: use heuristic but reduce confidence
        return new CleanupSuggestion
        {
            FilePath = folderPath,
            SizeBytes = heuristic.SizeBytes,
            Category = heuristic.Category,
            SafeToDelete = heuristic.SafeToDelete,
            Confidence = heuristic.Confidence * 0.7, // Penalize for failed AI
            Reason = $"[AI-Parse-Failed] {heuristic.Reason}",
            AutoApprove = false
        };
    }

    public async Task<List<CleanupSuggestion>> AnalyzeFilesAsync(
        List<string> filePaths,
        BrainSessionContext? sessionContext = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("📦 Batch analysis: {Count} files (Hybrid: Heuristics → AI)", filePaths.Count);

        var suggestions = new List<CleanupSuggestion>();

        // Group by directory
        var filesByDir = filePaths
            .GroupBy(f => Path.GetDirectoryName(f) ?? "")
            .Take(25); // Limit to prevent overload

        var dirCount = 0;
        var totalDirs = filesByDir.Count();

        foreach (var group in filesByDir)
        {
            cancellationToken.ThrowIfCancellationRequested();
            dirCount++;
            _logger.LogInformation("📂 [{Current}/{Total}] Analyzing: {Dir}", dirCount, totalDirs, group.Key);

            var fileNames = group.Select(Path.GetFileName).Where(n => n != null).Cast<string>().ToList();
            var suggestion = await AnalyzeFolderAsync(
                group.Key,
                fileNames,
                sessionContext,
                cancellationToken);
            suggestions.Add(suggestion);
        }

        sw.Stop();
        var stats = GetStats();
        _logger.LogInformation(
            "BATCH COMPLETE: {Duration}ms | Folders: {FolderCount} | Safe: {SafeCount} | AI: {AiCount} | Heuristic: {HeuristicCount}",
            sw.ElapsedMilliseconds,
            suggestions.Count,
            suggestions.Count(s => s.SafeToDelete),
            stats.ModelDecisions,
            stats.HeuristicOnly);

        return suggestions;
    }

    #region Deep Scan AI Analysis

    public async Task<DeepScanAiDecision> AnalyzeAppRemovalAsync(
        InstalledApp app,
        List<DeepScanMemory> similarDecisions,
        AppRemovalPattern publisherPattern,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _totalAnalyses++;

        _logger.LogDebug("🔍 Analyzing app for removal: {App}", app.Name);

        // If model not loaded, return heuristic-based decision
        if (!_isModelLoaded || _model == null || _tokenizer == null)
        {
            _heuristicOnly++;
            return CreateAppHeuristicDecision(app, similarDecisions, publisherPattern);
        }

        try
        {
            var prompt = BuildAppRemovalPrompt(app, similarDecisions, publisherPattern);
            var aiResult = await RunInferenceAsync(prompt, cancellationToken);
            var decision = ParseAppRemovalResponse(aiResult, app);
            _modelDecisions++;
            return decision;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI inference failed for app {App}, falling back to heuristics", app.Name);
            _heuristicOnly++;
            return CreateAppHeuristicDecision(app, similarDecisions, publisherPattern);
        }
    }

    public async Task<DeepScanAiDecision> AnalyzeRelocationAsync(
        FileCluster cluster,
        List<DeepScanMemory> similarDecisions,
        FileRelocationPattern fileTypePattern,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _totalAnalyses++;

        _logger.LogDebug("🔍 Analyzing files for relocation: {Path}", cluster.BasePath);

        // If model not loaded, return heuristic-based decision
        if (!_isModelLoaded || _model == null || _tokenizer == null)
        {
            _heuristicOnly++;
            return CreateRelocationHeuristicDecision(cluster, similarDecisions, fileTypePattern);
        }

        try
        {
            var prompt = BuildRelocationPrompt(cluster, similarDecisions, fileTypePattern);
            var aiResult = await RunInferenceAsync(prompt, cancellationToken);
            var decision = ParseRelocationResponse(aiResult, cluster, fileTypePattern);
            _modelDecisions++;
            return decision;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI inference failed for cluster {Path}, falling back to heuristics", cluster.BasePath);
            _heuristicOnly++;
            return CreateRelocationHeuristicDecision(cluster, similarDecisions, fileTypePattern);
        }
    }

    private string BuildAppRemovalPrompt(InstalledApp app, List<DeepScanMemory> memories, AppRemovalPattern pattern)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<|system|>");
        sb.AppendLine("You are SentinAI, a Windows storage optimization assistant. Analyze whether an app should be removed.");
        sb.AppendLine("Consider: bloatware status, system requirements, usage patterns, size impact, user learning patterns.");
        sb.AppendLine("Respond ONLY with JSON: {\"should_remove\":true/false,\"confidence\":0.0-1.0,\"category\":\"Bloatware|Unused|LargeUnused|KeepRecommended|Optional\",\"reason\":\"brief reason\"}");
        sb.AppendLine("<|end|>");
        sb.AppendLine("<|user|>");
        sb.AppendLine($"App: {app.Name}");
        sb.AppendLine($"Publisher: {app.Publisher}");
        sb.AppendLine($"Size: {app.TotalSizeFormatted}");
        sb.AppendLine($"Is Bloatware: {app.IsBloatware}");
        sb.AppendLine($"Is System App: {app.IsSystemApp}");
        sb.AppendLine($"Days Since Last Use: {app.DaysSinceLastUse}");

        if (memories.Count > 0)
        {
            var agreementRate = memories.Count(m => m.UserAgreed) * 100 / memories.Count;
            sb.AppendLine($"Similar past decisions: {memories.Count} ({agreementRate}% user agreement)");
        }

        if (pattern.TotalDecisions > 0)
        {
            sb.AppendLine($"Publisher pattern: {pattern.RemovalRate:P0} apps from this publisher were removed");
        }

        sb.AppendLine("<|end|>");
        sb.AppendLine("<|assistant|>");

        return sb.ToString();
    }

    private string BuildRelocationPrompt(FileCluster cluster, List<DeepScanMemory> memories, FileRelocationPattern pattern)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<|system|>");
        sb.AppendLine("You are SentinAI. Analyze whether files should be relocated to free up space on the primary drive.");
        sb.AppendLine("Consider: file type, cluster size, user preferences for file locations, available drives.");
        sb.AppendLine("Respond ONLY with JSON: {\"should_relocate\":true/false,\"priority\":1-5,\"target_drive\":\"X:\",\"confidence\":0.0-1.0,\"reason\":\"brief reason\"}");
        sb.AppendLine("<|end|>");
        sb.AppendLine("<|user|>");
        sb.AppendLine($"Cluster: {cluster.Name}");
        sb.AppendLine($"Type: {cluster.Type}");
        sb.AppendLine($"Size: {cluster.TotalSizeFormatted}");
        sb.AppendLine($"Source Drive: {cluster.BasePath?.Substring(0, 2)}");

        var availableDrives = string.Join(", ", cluster.AvailableDrives.Select(d => $"{d.Letter} ({d.FreeSpaceFormatted} free)"));
        sb.AppendLine($"Available Target Drives: {availableDrives}");

        if (!string.IsNullOrEmpty(pattern.PreferredTargetDrive))
        {
            sb.AppendLine($"User preference: {pattern.PreferredTargetDrive} for {cluster.PrimaryFileType} files");
        }

        if (memories.Count > 0)
        {
            sb.AppendLine($"Similar past decisions: {memories.Count}");
        }

        sb.AppendLine("<|end|>");
        sb.AppendLine("<|assistant|>");

        return sb.ToString();
    }

    private async Task<string> RunInferenceAsync(string prompt, CancellationToken cancellationToken)
    {
        await _modelLock.WaitAsync(cancellationToken);
        try
        {
            var sequences = _tokenizer!.Encode(prompt);
            var inputLength = sequences.NumSequences > 0 ? sequences[0].Length : 0;

            var maxInputLength = _config.MaxSequenceLength - _config.MaxOutputTokens;
            if (inputLength >= maxInputLength)
            {
                throw new InvalidOperationException($"Input too long ({inputLength} tokens)");
            }

            using var generatorParams = new GeneratorParams(_model!);
            generatorParams.SetSearchOption("max_length", _config.MaxSequenceLength);
            generatorParams.SetSearchOption("temperature", _config.Temperature);
            generatorParams.SetSearchOption("top_p", 0.9);
            generatorParams.SetInputSequences(sequences);

            var outputTokens = new List<int>();
            var timeoutMs = _config.InferenceTimeoutSeconds * 1000;

            using var generator = new Generator(_model!, generatorParams);
            var sw = Stopwatch.StartNew();

            while (!generator.IsDone())
            {
                generator.ComputeLogits();
                generator.GenerateNextToken();

                var seq = generator.GetSequence(0);
                if (seq.Length > 0)
                {
                    outputTokens.Add(seq[^1]);
                }

                if (sw.ElapsedMilliseconds > timeoutMs)
                {
                    _logger.LogWarning("AI generation timeout after {Timeout}s", _config.InferenceTimeoutSeconds);
                    break;
                }
            }

            return _tokenizer!.Decode(outputTokens.ToArray());
        }
        finally
        {
            _modelLock.Release();
        }
    }

    private DeepScanAiDecision ParseAppRemovalResponse(string response, InstalledApp app)
    {
        try
        {
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');

            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonStr = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                using var doc = JsonDocument.Parse(jsonStr);
                var root = doc.RootElement;

                var shouldRemove = root.TryGetProperty("should_remove", out var removeProp) && removeProp.GetBoolean();
                var confidence = root.TryGetProperty("confidence", out var confProp) ? confProp.GetDouble() : 0.5;
                var category = root.TryGetProperty("category", out var catProp) ? catProp.GetString() : "Optional";
                var reason = root.TryGetProperty("reason", out var reasonProp) ? reasonProp.GetString() ?? "" : "";

                _logger.LogInformation("🎯 AI app decision: remove={Remove}, confidence={Conf:P0}, category={Category}",
                    shouldRemove, confidence, category);

                return new DeepScanAiDecision
                {
                    ShouldProceed = shouldRemove,
                    Confidence = confidence,
                    Category = category,
                    Reason = $"[AI] {reason}",
                    IsAiDecision = true
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse AI response for app {App}", app.Name);
        }

        // Fallback to heuristic
        return CreateAppHeuristicDecision(app, new List<DeepScanMemory>(), new AppRemovalPattern());
    }

    private DeepScanAiDecision ParseRelocationResponse(string response, FileCluster cluster, FileRelocationPattern pattern)
    {
        try
        {
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');

            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonStr = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                using var doc = JsonDocument.Parse(jsonStr);
                var root = doc.RootElement;

                var shouldRelocate = root.TryGetProperty("should_relocate", out var relocateProp) && relocateProp.GetBoolean();
                var priority = root.TryGetProperty("priority", out var priProp) ? priProp.GetInt32() : 3;
                var targetDrive = root.TryGetProperty("target_drive", out var driveProp) ? driveProp.GetString() : null;
                var confidence = root.TryGetProperty("confidence", out var confProp) ? confProp.GetDouble() : 0.5;
                var reason = root.TryGetProperty("reason", out var reasonProp) ? reasonProp.GetString() ?? "" : "";

                _logger.LogInformation("🎯 AI relocation decision: relocate={Relocate}, priority={Priority}, target={Target}",
                    shouldRelocate, priority, targetDrive);

                return new DeepScanAiDecision
                {
                    ShouldProceed = shouldRelocate,
                    Confidence = confidence,
                    Priority = priority,
                    TargetDrive = targetDrive,
                    Reason = $"[AI] {reason}",
                    IsAiDecision = true
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse AI response for cluster {Path}", cluster.BasePath);
        }

        // Fallback to heuristic
        return CreateRelocationHeuristicDecision(cluster, new List<DeepScanMemory>(), pattern);
    }

    private DeepScanAiDecision CreateAppHeuristicDecision(InstalledApp app, List<DeepScanMemory> memories, AppRemovalPattern pattern)
    {
        // Bloatware detection
        if (app.IsBloatware)
        {
            return new DeepScanAiDecision
            {
                ShouldProceed = true,
                Confidence = 0.9,
                Category = "Bloatware",
                Reason = "[Heuristic] Detected as bloatware/pre-installed unnecessary software",
                IsAiDecision = false
            };
        }

        // System app protection
        if (app.IsSystemApp)
        {
            return new DeepScanAiDecision
            {
                ShouldProceed = false,
                Confidence = 0.95,
                Category = "KeepRecommended",
                Reason = "[Heuristic] System app - required for Windows functionality",
                IsAiDecision = false
            };
        }

        // Publisher pattern
        if (pattern.TotalDecisions >= 3 && pattern.RemovalRate > 0.8)
        {
            return new DeepScanAiDecision
            {
                ShouldProceed = true,
                Confidence = 0.85,
                Category = "Bloatware",
                Reason = $"[Heuristic] User typically removes apps from {app.Publisher} ({pattern.RemovalRate:P0} removal rate)",
                IsAiDecision = false
            };
        }

        // Unused apps
        if (app.IsUnused && app.DaysSinceLastUse > 90)
        {
            var confidence = app.DaysSinceLastUse > 180 ? 0.85 : 0.7;
            return new DeepScanAiDecision
            {
                ShouldProceed = true,
                Confidence = confidence,
                Category = "Unused",
                Reason = $"[Heuristic] Not used in {app.DaysSinceLastUse} days",
                IsAiDecision = false
            };
        }

        // Large unused apps
        if (app.TotalSizeBytes > 1024L * 1024 * 1024 && app.IsUnused)
        {
            return new DeepScanAiDecision
            {
                ShouldProceed = true,
                Confidence = 0.75,
                Category = "LargeUnused",
                Reason = $"[Heuristic] Large app ({app.TotalSizeFormatted}) not used in {app.DaysSinceLastUse} days",
                IsAiDecision = false
            };
        }

        // Default: keep
        return new DeepScanAiDecision
        {
            ShouldProceed = false,
            Confidence = 0.6,
            Category = "Optional",
            Reason = "[Heuristic] App appears to be in use or recently accessed",
            IsAiDecision = false
        };
    }

    private DeepScanAiDecision CreateRelocationHeuristicDecision(FileCluster cluster, List<DeepScanMemory> memories, FileRelocationPattern pattern)
    {
        if (!cluster.CanRelocate)
        {
            return new DeepScanAiDecision
            {
                ShouldProceed = false,
                Confidence = 0.95,
                Priority = 1,
                Reason = "[Heuristic] Files cannot be safely relocated",
                IsAiDecision = false
            };
        }

        // Determine priority by size
        int priority;
        if (cluster.TotalBytes > 50L * 1024 * 1024 * 1024)
            priority = 5;
        else if (cluster.TotalBytes > 10L * 1024 * 1024 * 1024)
            priority = 4;
        else if (cluster.TotalBytes > 1L * 1024 * 1024 * 1024)
            priority = 3;
        else
            priority = 2;

        // Determine target drive
        var targetDrive = !string.IsNullOrEmpty(pattern.PreferredTargetDrive)
            ? pattern.PreferredTargetDrive
            : cluster.AvailableDrives.FirstOrDefault()?.Letter;

        var reason = cluster.Type switch
        {
            FileClusterType.MediaVideos => $"[Heuristic] Large video files ({cluster.TotalSizeFormatted}) - good candidate for relocation",
            FileClusterType.MediaPhotos => $"[Heuristic] Photo collection ({cluster.TotalSizeFormatted}) - can be moved to free space",
            FileClusterType.GameAssets => $"[Heuristic] Game files ({cluster.TotalSizeFormatted}) - relocatable with junction",
            FileClusterType.Downloads => $"[Heuristic] Downloads folder ({cluster.TotalSizeFormatted}) - consider organizing",
            FileClusterType.Archives => $"[Heuristic] Archive files ({cluster.TotalSizeFormatted}) - safe to relocate",
            _ => $"[Heuristic] Files ({cluster.TotalSizeFormatted}) can be relocated to free space"
        };

        return new DeepScanAiDecision
        {
            ShouldProceed = targetDrive != null,
            Confidence = 0.7 + (memories.Count * 0.05),
            Priority = priority,
            TargetDrive = targetDrive,
            Reason = reason,
            IsAiDecision = false
        };
    }

    #endregion

    public void Dispose()
    {
        var stats = GetStats();
        _logger.LogInformation(
            "🗑️ AgentBrain disposing. Final stats: Total={Total}, AI={AI}, Heuristic={Heuristic}, Safe={Safe}",
            stats.TotalAnalyses, stats.ModelDecisions, stats.HeuristicOnly, stats.SafeToDeleteCount);

        _modelLock.Dispose();
        _tokenizer?.Dispose();
        _model?.Dispose();
        _isInitialized = false;
        _isModelLoaded = false;
    }
}
