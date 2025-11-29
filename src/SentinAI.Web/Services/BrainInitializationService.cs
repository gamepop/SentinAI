using System.Diagnostics;
using SentinAI.Web.Services.Rag;

namespace SentinAI.Web.Services;

/// <summary>
/// Background service to initialize the Brain on startup
/// Downloads the AI model if needed and initializes the analysis engine
/// </summary>
public class BrainInitializationService : BackgroundService
{
    private readonly ILogger<BrainInitializationService> _logger;
    private readonly IAgentBrain _brain;
    private readonly IModelDownloadService _modelDownloader;
    private readonly IWinapp2Parser _winapp2Parser;
    private readonly IRagStore _ragStore;

    public BrainInitializationService(
        ILogger<BrainInitializationService> logger,
        IAgentBrain brain,
        IModelDownloadService modelDownloader,
        IWinapp2Parser winapp2Parser,
        IRagStore ragStore)
    {
        _logger = logger;
        _brain = brain;
        _modelDownloader = modelDownloader;
        _winapp2Parser = winapp2Parser;
        _ragStore = ragStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sw = Stopwatch.StartNew();

        _logger.LogInformation("═══════════════════════════════════════════════════════════════");
        _logger.LogInformation("🧠 BRAIN INITIALIZATION STARTING");
        _logger.LogInformation("═══════════════════════════════════════════════════════════════");

        try
        {
            // Get model path from ModelDownloadService (uses correct provider-specific path)
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var modelPath = _modelDownloader.GetModelPath();
            var executionProvider = _modelDownloader.GetExecutionProvider();

            _logger.LogInformation("📁 Model path: {Path}", modelPath);
            _logger.LogInformation("🖥️ Execution Provider: {Provider}", executionProvider);

            // Check model status
            var modelExists = _modelDownloader.IsModelDownloaded();
            _logger.LogInformation("📦 Model status: {Status}", modelExists ? "CACHED" : "NOT FOUND");

            if (!modelExists)
            {
                _logger.LogInformation("⬇️ Downloading Phi-4 Mini model ({Provider})... (this may take a while)", executionProvider);
                var downloadStart = sw.ElapsedMilliseconds;

                try
                {
                    await _modelDownloader.DownloadModelAsync(stoppingToken);
                    var downloadDuration = sw.ElapsedMilliseconds - downloadStart;
                    _logger.LogInformation("✅ Model downloaded in {Duration}ms", downloadDuration);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ Model download failed - Brain will run in heuristic mode");
                }
            }
            else
            {
                _logger.LogInformation("✅ Phi-4 Mini model ({Provider}) already cached at {Path}", executionProvider, modelPath);

                // List model files
                if (Directory.Exists(modelPath))
                {
                    var files = Directory.GetFiles(modelPath);
                    _logger.LogInformation("📄 Model files ({Count}):", files.Length);
                    foreach (var file in files.Take(10))
                    {
                        var fi = new FileInfo(file);
                        _logger.LogInformation("   • {Name} ({Size:N0} bytes)", fi.Name, fi.Length);
                    }
                }
            }

            // Initialize the Brain
            _logger.LogInformation("───────────────────────────────────────────────────────────────");
            _logger.LogInformation("🔧 Initializing Brain analysis engine ({Provider})...", executionProvider);

            var initStart = sw.ElapsedMilliseconds;
            var success = await _brain.InitializeAsync(modelPath);
            var initDuration = sw.ElapsedMilliseconds - initStart;

            if (success)
            {
                _logger.LogInformation("✅ Brain initialized in {Duration}ms", initDuration);
                _logger.LogInformation("   Mode: {Mode}", _brain.IsReady ? "ACTIVE" : "INACTIVE");
                _logger.LogInformation("   AI Enabled: {AI}", _brain.IsModelLoaded);
            }
            else
            {
                _logger.LogWarning("⚠️ Brain initialization failed after {Duration}ms", initDuration);
                _logger.LogWarning("   Analysis requests will return empty suggestions");
            }

            // Load Winapp2 database
            _logger.LogInformation("───────────────────────────────────────────────────────────────");
            var winapp2Path = Path.Combine(localAppData, "SentinAI", "Winapp2.ini");
            _logger.LogInformation("📋 Loading Winapp2 cleanup rules from: {Path}", winapp2Path);

            var winapp2Start = sw.ElapsedMilliseconds;
            await _winapp2Parser.LoadAsync(winapp2Path);
            var winapp2Duration = sw.ElapsedMilliseconds - winapp2Start;

            _logger.LogInformation("✅ Winapp2 rules loaded in {Duration}ms", winapp2Duration);

            if (_ragStore.IsEnabled)
            {
                _logger.LogInformation("───────────────────────────────────────────────────────────────");
                _logger.LogInformation("📚 Initializing RAG store (provider: Weaviate)...");
                var ragStart = sw.ElapsedMilliseconds;
                await _ragStore.InitializeAsync(stoppingToken);
                var ragDuration = sw.ElapsedMilliseconds - ragStart;
                _logger.LogInformation("✅ RAG store ready in {Duration}ms", ragDuration);
            }
            else
            {
                _logger.LogInformation("📚 RAG store disabled (set RagStore:Enabled=true to turn it on).");
            }

            sw.Stop();
            _logger.LogInformation("═══════════════════════════════════════════════════════════════");
            _logger.LogInformation("🧠 BRAIN INITIALIZATION COMPLETE in {Duration}ms", sw.ElapsedMilliseconds);
            _logger.LogInformation("   • Brain Ready: {Ready}", _brain.IsReady);
            _logger.LogInformation("   • AI Mode: {AI}", _brain.IsModelLoaded ? $"ENABLED ({executionProvider})" : "DISABLED (Heuristics Only)");
            _logger.LogInformation("═══════════════════════════════════════════════════════════════");
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "❌ Brain initialization failed after {Duration}ms", sw.ElapsedMilliseconds);
            _logger.LogError("   Brain service will not be available");
        }
    }
}
