using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntimeGenAI;
using SentinAI.Shared.Models;
using System.Diagnostics;
using System.Text.Json;

namespace SentinAI.Brain.Services;

public interface IAgentBrain
{
    Task<bool> InitializeAsync(string modelPath);
    Task<CleanupSuggestion> AnalyzeFolderAsync(string folderPath, List<string> fileNames);
    Task<List<CleanupSuggestion>> AnalyzeFilesAsync(List<string> filePaths);
    bool IsReady { get; }
    
    /// <summary>
    /// Event fired when a model interaction occurs, for observability
    /// </summary>
    event EventHandler<ModelInteractionEventArgs>? ModelInteraction;
}

public class ModelInteractionEventArgs : EventArgs
{
    public required string InteractionType { get; init; }
    public string? Prompt { get; init; }
    public string? Response { get; init; }
    public string? FolderPath { get; init; }
    public int? FileCount { get; init; }
    public long DurationMs { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>
/// The "Mind" - Runs Phi-4 Mini model for intelligent file analysis
/// Uses ONNX Runtime GenAI with DirectML for GPU/NPU acceleration
/// </summary>
public class AgentBrain : IAgentBrain, IDisposable
{
    private readonly ILogger<AgentBrain>? _logger;
    private Model? _model;
    private Tokenizer? _tokenizer;
    private bool _isInitialized;
    private string? _modelPath;
    private int _totalInteractions;
    private int _successfulInteractions;

    public bool IsReady => _isInitialized && _model != null;
    
    public event EventHandler<ModelInteractionEventArgs>? ModelInteraction;

    public AgentBrain(ILogger<AgentBrain>? logger = null)
    {
        _logger = logger;
    }

    public async Task<bool> InitializeAsync(string modelPath)
    {
        var sw = Stopwatch.StartNew();
        
        try
        {
            _logger?.LogInformation("\ud83d\ude80 Initializing AI model from: {ModelPath}", modelPath);
            
            if (!Directory.Exists(modelPath))
            {
                var error = $"Model path not found: {modelPath}";
                _logger?.LogError("\u274c Model initialization failed: {Error}", error);
                OnModelInteraction(new ModelInteractionEventArgs
                {
                    InteractionType = "Initialization",
                    Success = false,
                    ErrorMessage = error,
                    DurationMs = sw.ElapsedMilliseconds
                });
                throw new DirectoryNotFoundException(error);
            }

            // List model files for diagnostics
            var modelFiles = Directory.GetFiles(modelPath, "*.*", SearchOption.TopDirectoryOnly);
            _logger?.LogInformation("\ud83d\udcc1 Model directory contains {Count} files: {Files}", 
                modelFiles.Length, 
                string.Join(", ", modelFiles.Select(Path.GetFileName).Take(10)));

            // Load ONNX model with DirectML execution provider
            _logger?.LogInformation("\u2699\ufe0f Loading ONNX model...");
            _model = new Model(modelPath);
            
            _logger?.LogInformation("\ud83d\udcdd Creating tokenizer...");
            _tokenizer = new Tokenizer(_model);

            _isInitialized = true;
            _modelPath = modelPath;
            
            sw.Stop();
            _logger?.LogInformation("\u2705 Model initialized successfully in {Duration}ms", sw.ElapsedMilliseconds);
            
            OnModelInteraction(new ModelInteractionEventArgs
            {
                InteractionType = "Initialization",
                Success = true,
                DurationMs = sw.ElapsedMilliseconds,
                Metadata = new Dictionary<string, string>
                {
                    ["modelPath"] = modelPath,
                    ["fileCount"] = modelFiles.Length.ToString()
                }
            });
            
            return true;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.LogError(ex, "\u274c Failed to initialize AI model after {Duration}ms: {Message}", 
                sw.ElapsedMilliseconds, ex.Message);
            
            OnModelInteraction(new ModelInteractionEventArgs
            {
                InteractionType = "Initialization",
                Success = false,
                ErrorMessage = ex.Message,
                DurationMs = sw.ElapsedMilliseconds
            });
            
            return false;
        }
    }

    public async Task<CleanupSuggestion> AnalyzeFolderAsync(
        string folderPath,
        List<string> fileNames)
    {
        _totalInteractions++;
        var sw = Stopwatch.StartNew();
        
        if (!IsReady)
        {
            var error = "Model not initialized";
            _logger?.LogError("\u274c {Error} - cannot analyze folder: {Folder}", error, folderPath);
            OnModelInteraction(new ModelInteractionEventArgs
            {
                InteractionType = "Analysis",
                FolderPath = folderPath,
                FileCount = fileNames.Count,
                Success = false,
                ErrorMessage = error,
                DurationMs = sw.ElapsedMilliseconds
            });
            throw new InvalidOperationException(error);
        }

        // Limit file list to avoid token overflow
        var fileList = string.Join(", ", fileNames.Take(20));
        if (fileNames.Count > 20)
        {
            fileList += $" ... and {fileNames.Count - 20} more";
        }

        _logger?.LogInformation("\ud83d\udd0d Analyzing folder: {Folder} ({FileCount} files)", folderPath, fileNames.Count);
        _logger?.LogDebug("Files: {FileList}", fileList);

        var prompt = BuildAnalysisPrompt(folderPath, fileList);
        
        // Log prompt (truncated for readability)
        var promptPreview = prompt.Length > 500 ? prompt[..500] + "..." : prompt;
        _logger?.LogDebug("\ud83d\udcac Prompt:\n{Prompt}", promptPreview);
        
        string response;
        try
        {
            response = await GenerateResponseAsync(prompt);
            sw.Stop();
            
            _logger?.LogInformation("\ud83e\udd16 Model response received in {Duration}ms", sw.ElapsedMilliseconds);
            _logger?.LogDebug("\ud83d\udce4 Raw response:\n{Response}", response);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.LogError(ex, "\u274c Model generation failed for {Folder} after {Duration}ms", 
                folderPath, sw.ElapsedMilliseconds);
            
            OnModelInteraction(new ModelInteractionEventArgs
            {
                InteractionType = "Analysis",
                Prompt = promptPreview,
                FolderPath = folderPath,
                FileCount = fileNames.Count,
                Success = false,
                ErrorMessage = ex.Message,
                DurationMs = sw.ElapsedMilliseconds
            });
            
            throw;
        }

        var suggestion = ParseCleanupSuggestion(response);
        
        _logger?.LogInformation(
            "\u2705 Analysis complete: SafeToDelete={Safe}, Category={Category}, Confidence={Confidence:P0}, Reason={Reason}",
            suggestion.SafeToDelete,
            suggestion.Category,
            suggestion.Confidence,
            suggestion.Reason);
        
        _successfulInteractions++;
        
        OnModelInteraction(new ModelInteractionEventArgs
        {
            InteractionType = "Analysis",
            Prompt = promptPreview,
            Response = response,
            FolderPath = folderPath,
            FileCount = fileNames.Count,
            Success = true,
            DurationMs = sw.ElapsedMilliseconds,
            Metadata = new Dictionary<string, string>
            {
                ["safeToDelete"] = suggestion.SafeToDelete.ToString(),
                ["category"] = suggestion.Category,
                ["confidence"] = suggestion.Confidence.ToString("F2"),
                ["autoApprove"] = suggestion.AutoApprove.ToString(),
                ["reason"] = suggestion.Reason
            }
        });

        return suggestion;
    }

    public async Task<List<CleanupSuggestion>> AnalyzeFilesAsync(List<string> filePaths)
    {
        if (!IsReady)
        {
            _logger?.LogError("\u274c Model not initialized - cannot analyze {Count} files", filePaths.Count);
            throw new InvalidOperationException("Model not initialized");
        }

        _logger?.LogInformation("\ud83d\udce6 Batch analyzing {Count} files across multiple directories", filePaths.Count);
        
        var suggestions = new List<CleanupSuggestion>();

        // Group files by directory for batch analysis
        var filesByDir = filePaths
            .GroupBy(f => Path.GetDirectoryName(f) ?? "")
            .Take(10); // Limit to 10 directories at a time

        var dirCount = 0;
        foreach (var group in filesByDir)
        {
            dirCount++;
            var fileNames = group.Select(Path.GetFileName).Where(n => n != null).Cast<string>().ToList();
            
            _logger?.LogInformation("\ud83d\udcc2 [{DirNum}/10] Analyzing directory: {Dir}", dirCount, group.Key);
            
            try
            {
                var suggestion = await AnalyzeFolderAsync(group.Key, fileNames);
                suggestions.Add(suggestion);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "\u26a0\ufe0f Failed to analyze directory {Dir}, skipping", group.Key);
            }
        }

        _logger?.LogInformation("\ud83c\udfc1 Batch analysis complete: {Analyzed}/{Total} directories processed, {Suggestions} suggestions generated",
            dirCount, filesByDir.Count(), suggestions.Count);

        return suggestions;
    }

    private string BuildAnalysisPrompt(string folderPath, string fileList)
    {
        return $@"<|system|>
You are a Windows Filesystem Expert AI. Your role is to analyze files and determine if they are safe to delete.

Safe to delete categories:
- Cache files (browser cache, app cache)
- Temporary files (*.tmp, temp folders)
- Log files (*.log)
- Build artifacts (bin, obj, node_modules older than 60 days)
- Old downloads (files in Downloads folder older than 90 days)

NEVER delete:
- User documents (*.docx, *.pdf, *.txt in Documents)
- Source code (*.cs, *.py, *.js unless in node_modules)
- Configuration files
- System files

Return ONLY valid JSON in this exact format:
{{
  ""safe_to_delete"": true/false,
  ""reason"": ""brief explanation"",
  ""category"": ""cache/logs/temp/node_modules/build_artifacts/downloads/user_data/system_files"",
  ""confidence"": 0.0-1.0,
  ""auto_approve"": true/false
}}
<|end|>
<|user|>
Analyze this folder: {folderPath}

Files: {fileList}

Is this safe to delete? Return JSON only.
<|end|>
<|assistant|>";
    }

    private async Task<string> GenerateResponseAsync(string prompt)
    {
        if (_model == null || _tokenizer == null)
        {
            throw new InvalidOperationException("Model not initialized");
        }

        _logger?.LogDebug("\ud83d\udd04 Tokenizing input ({Length} chars)...", prompt.Length);
        
        // Tokenize input
        var sequences = _tokenizer.Encode(prompt);
        
        _logger?.LogDebug("\ud83c\udfb0 Input tokenized, setting up generation parameters...");

        // Setup generation parameters
        using var generatorParams = new GeneratorParams(_model);
        generatorParams.SetSearchOption("max_length", 1024);
        generatorParams.SetSearchOption("temperature", 0.3); // Lower temperature for more deterministic output
        generatorParams.SetSearchOption("top_p", 0.9);
        generatorParams.SetInputSequences(sequences);

        _logger?.LogDebug("\u2699\ufe0f Generation params: max_length=1024, temperature=0.3, top_p=0.9");
        _logger?.LogInformation("\ud83e\udd16 Starting model inference...");
        
        var sw = Stopwatch.StartNew();
        
        // Generate response
        var outputSequences = await Task.Run(() =>
        {
            using var generator = new Generator(_model, generatorParams);
            var tokenCount = 0;
            while (!generator.IsDone())
            {
                generator.ComputeLogits();
                generator.GenerateNextToken();
                tokenCount++;
                
                // Log progress every 50 tokens
                if (tokenCount % 50 == 0)
                {
                    _logger?.LogDebug("\ud83d\udce1 Generated {Count} tokens...", tokenCount);
                }
            }
            _logger?.LogDebug("\u2705 Generation complete: {Count} tokens", tokenCount);
            return generator.GetSequence(0);
        });

        sw.Stop();
        _logger?.LogInformation("\ud83c\udfc1 Model inference completed in {Duration}ms", sw.ElapsedMilliseconds);

        var output = _tokenizer.Decode(outputSequences);

        // Extract JSON from response (remove any markdown formatting)
        output = output.Replace("```json", "").Replace("```", "").Trim();
        
        // Try to extract just the JSON part (after the prompt)
        var jsonStart = output.LastIndexOf('{');
        var jsonEnd = output.LastIndexOf('}');
        if (jsonStart >= 0 && jsonEnd > jsonStart)
        {
            output = output.Substring(jsonStart, jsonEnd - jsonStart + 1);
            _logger?.LogDebug("\ud83d\udccb Extracted JSON: {Json}", output);
        }

        return output;
    }

    private CleanupSuggestion ParseCleanupSuggestion(string jsonResponse)
    {
        try
        {
            _logger?.LogDebug("\ud83d\udd0d Parsing JSON response...");
            
            // Try to parse the JSON response
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            var suggestion = JsonSerializer.Deserialize<CleanupSuggestion>(jsonResponse, options);
            
            if (suggestion != null)
            {
                _logger?.LogDebug("\u2705 Successfully parsed suggestion: SafeToDelete={Safe}", suggestion.SafeToDelete);
                return suggestion;
            }
            
            _logger?.LogWarning("\u26a0\ufe0f JSON parsed but result was null, using default");
            return CreateDefaultSuggestion();
        }
        catch (JsonException ex)
        {
            // If parsing fails, return a conservative default
            _logger?.LogWarning(ex, "\u26a0\ufe0f Failed to parse JSON response: {Response}", jsonResponse);
            return CreateDefaultSuggestion();
        }
    }

    private CleanupSuggestion CreateDefaultSuggestion()
    {
        _logger?.LogDebug("\ud83d\udee1\ufe0f Creating conservative default suggestion (SafeToDelete=false)");
        return new CleanupSuggestion
        {
            SafeToDelete = false,
            Reason = "Unable to analyze - defaulting to safe mode",
            Category = CleanupCategories.Unknown,
            Confidence = 0.0,
            AutoApprove = false
        };
    }
    
    private void OnModelInteraction(ModelInteractionEventArgs args)
    {
        try
        {
            ModelInteraction?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error in ModelInteraction event handler");
        }
    }
    
    /// <summary>
    /// Gets statistics about model usage
    /// </summary>
    public (int Total, int Successful, double SuccessRate) GetStats()
    {
        var rate = _totalInteractions > 0 
            ? (double)_successfulInteractions / _totalInteractions 
            : 0.0;
        return (_totalInteractions, _successfulInteractions, rate);
    }

    public void Dispose()
    {
        _logger?.LogInformation("\ud83d\udeae Disposing AgentBrain (Total: {Total}, Successful: {Successful})", 
            _totalInteractions, _successfulInteractions);
        _tokenizer?.Dispose();
        _model?.Dispose();
        _isInitialized = false;
    }
}
