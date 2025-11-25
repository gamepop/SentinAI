using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SentinAI.Web.Services;

public interface IModelDownloadService
{
    Task DownloadModelAsync(CancellationToken cancellationToken, bool forceRedownload = false);
    bool IsModelDownloaded();
    string GetModelPath();
    string GetExecutionProvider();
}

/// <summary>
/// Handles on-demand downloading of the Phi-3 ONNX model
/// Downloads from HuggingFace - supports both CPU and DirectML variants
/// </summary>
public class ModelDownloadService : IModelDownloadService
{
    private const string MODEL_REPO = "microsoft/Phi-3-mini-4k-instruct-onnx";

    // These are set dynamically based on execution provider
    private readonly string[] _modelFiles;

    private static readonly string[] CONFIG_FILES =
    {
        "config.json",
        "tokenizer.json",
        "tokenizer_config.json",
        "special_tokens_map.json",
        "tokenizer.model",
        "added_tokens.json",
        "genai_config.json"
    };

    private readonly string _localModelPath;
    private readonly string _modelSubdirectory;
    private readonly ILogger<ModelDownloadService> _logger;
    private readonly bool _forceRedownload;
    private readonly TelemetryClient _telemetry;
    private readonly BrainConfiguration _config;

    public ModelDownloadService(
        ILogger<ModelDownloadService> logger,
        IConfiguration configuration,
        IOptions<BrainConfiguration> brainConfig,
        TelemetryClient telemetryClient)
    {
        _logger = logger;
        _config = brainConfig.Value;
        _forceRedownload = _config.ForceModelRedownload || ResolveForceFlag(configuration);
        _telemetry = telemetryClient;
        _modelSubdirectory = _config.GetModelSubdirectory();

        // Get the correct model file names based on execution provider
        var (onnxFile, onnxDataFile) = _config.GetModelFileNames();
        _modelFiles = new[] { onnxFile, onnxDataFile };

        // Store model in LocalApplicationData with provider-specific subfolder
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var providerFolder = _config.UseCpu ? "Phi3-Mini-CPU" : "Phi3-Mini-DirectML";

        _localModelPath = !string.IsNullOrEmpty(_config.ModelPath)
            ? _config.ModelPath
            : Path.Combine(appData, "SentinAI", "Models", providerFolder);

        _logger.LogInformation("🧠 Model Download Service initialized:");
        _logger.LogInformation("   • Execution Provider: {Provider}", _config.GetProviderDisplayName());
        _logger.LogInformation("   • Model Subdirectory: {Subdir}", _modelSubdirectory);
        _logger.LogInformation("   • Model Files: {Files}", string.Join(", ", _modelFiles));
        _logger.LogInformation("   • Local Path: {Path}", _localModelPath);

        if (_forceRedownload)
        {
            _logger.LogWarning("⚠️ Force model re-download flag detected. Existing cache will be cleared on startup.");
        }
    }

    public string GetModelPath() => _localModelPath;

    public string GetExecutionProvider() => _config.GetProviderDisplayName();

    public bool IsModelDownloaded()
    {
        if (!Directory.Exists(_localModelPath))
        {
            return false;
        }

        // Check for the provider-specific ONNX file (keep original filenames for genai_config.json compatibility)
        var (onnxFile, _) = _config.GetModelFileNames();
        var modelOnnxPath = Path.Combine(_localModelPath, onnxFile);
        try
        {
            var info = new FileInfo(modelOnnxPath);
            return info.Exists && info.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task DownloadModelAsync(CancellationToken cancellationToken, bool forceRedownload = false)
    {
        var targetPath = _localModelPath;
        var cacheReady = IsModelDownloaded();
        var effectiveForce = forceRedownload || _forceRedownload;

        var downloadId = Guid.NewGuid().ToString();
        TrackModelEvent("ModelDownloadRequested", new Dictionary<string, string>
        {
            ["downloadId"] = downloadId,
            ["forceFlag"] = effectiveForce.ToString(),
            ["cacheReady"] = cacheReady.ToString()
        });

        if (cacheReady && !effectiveForce)
        {
            _logger.LogInformation("✅ Phi-3 model ({Provider}) already cached at {Path}.", _config.GetProviderDisplayName(), targetPath);
            return;
        }

        if (effectiveForce && cacheReady)
        {
            _logger.LogInformation("Force re-download requested. Clearing cached model at {Path} before download.", targetPath);
        }

        CleanupLegacyArtifacts(targetPath);
        Directory.CreateDirectory(targetPath);

        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromHours(2); // Large model files need extended timeout

        var hfToken = Environment.GetEnvironmentVariable("HUGGINGFACE_TOKEN");
        if (!string.IsNullOrWhiteSpace(hfToken))
        {
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", hfToken);
        }

        // Don't use the passed cancellation token for long downloads - create our own
        using var downloadCts = new CancellationTokenSource(TimeSpan.FromHours(2));

        var totalBytes = 0L;

        try
        {
            _logger.LogInformation("📥 Downloading {Provider} model from HuggingFace...", _config.GetProviderDisplayName());
            _logger.LogInformation("   Subdirectory: {Subdir}", _modelSubdirectory);
            _logger.LogInformation("   Model files: {Files}", string.Join(", ", _modelFiles));

            // Download model files - keep original filenames for genai_config.json compatibility
            var (sourceOnnx, sourceOnnxData) = (_modelFiles[0], _modelFiles[1]);
            var dlToken = downloadCts.Token;

            // Download main ONNX model (keep original filename)
            var onnxUrl = BuildModelUrl(sourceOnnx, _modelSubdirectory);
            var onnxDest = Path.Combine(targetPath, sourceOnnx);
            _logger.LogInformation("⬇️ Downloading {FileName} (this may take several minutes)...", sourceOnnx);
            var onnxSize = await DownloadFileAsync(httpClient, onnxUrl, onnxDest, dlToken);
            totalBytes += onnxSize;
            _logger.LogInformation("✅ Downloaded {FileName} ({Size:N0} bytes).", sourceOnnx, onnxSize);

            // Download ONNX data file (keep original filename)
            var dataUrl = BuildModelUrl(sourceOnnxData, _modelSubdirectory);
            var dataDest = Path.Combine(targetPath, sourceOnnxData);
            _logger.LogInformation("⬇️ Downloading {FileName} (this is ~2GB, please wait)...", sourceOnnxData);
            var dataSize = await DownloadFileAsync(httpClient, dataUrl, dataDest, dlToken);
            totalBytes += dataSize;
            _logger.LogInformation("✅ Downloaded {FileName} ({Size:N0} bytes).", sourceOnnxData, dataSize);

            // Download tokenizer and config files
            totalBytes += await DownloadModelConfigAsync(httpClient, targetPath, dlToken);

            TrackModelEvent("ModelDownloadCompleted", new Dictionary<string, string>
            {
                ["downloadId"] = downloadId,
                ["forceFlag"] = effectiveForce.ToString()
            }, new Dictionary<string, double>
            {
                ["totalBytes"] = totalBytes
            });

            _logger.LogInformation("✅ Model download complete! Total: {Size:N0} bytes", totalBytes);
        }
        catch (Exception ex)
        {
            TrackModelEvent("ModelDownloadFailed", new Dictionary<string, string>
            {
                ["downloadId"] = downloadId,
                ["forceFlag"] = effectiveForce.ToString(),
                ["errorType"] = ex.GetType().Name
            });
            _logger.LogError(ex, "Model download failed.");
            throw;
        }
    }

    private async Task<long> DownloadModelConfigAsync(HttpClient httpClient, string targetPath, CancellationToken cancellationToken)
    {
        var total = 0L;

        foreach (var configFile in CONFIG_FILES)
        {
            try
            {
                var url = BuildModelUrl(configFile, _modelSubdirectory);
                var destination = Path.Combine(targetPath, configFile);
                var size = await DownloadFileAsync(httpClient, url, destination, cancellationToken);
                total += size;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not download auxiliary model file {ConfigFile}.", configFile);
                TrackModelEvent("ModelConfigDownloadFailed", new Dictionary<string, string>
                {
                    ["configFile"] = configFile,
                    ["errorType"] = ex.GetType().Name
                });
            }
        }

        return total;
    }

    private static string BuildModelUrl(string relativePath, string subdirectory)
        => $"https://huggingface.co/{MODEL_REPO}/resolve/main/{subdirectory}/{relativePath}";

    private void CleanupLegacyArtifacts(string targetPath)
    {
        if (!Directory.Exists(targetPath))
        {
            return;
        }

        try
        {
            Directory.Delete(targetPath, true);
            _logger.LogInformation("Deleted legacy Phi-3 cache at {Path}.", targetPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fully delete legacy model directory {Path}.", targetPath);

            foreach (var file in Directory.EnumerateFiles(targetPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception fileEx)
                {
                    _logger.LogWarning(fileEx, "Could not delete legacy file {File}.", file);
                }
            }
        }
    }

    private async Task<long> DownloadFileAsync(HttpClient httpClient, string url, string destinationPath, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0;
        var expectedHash = ExtractSha256(response);

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        IncrementalHash? hasher = null;

        if (!string.IsNullOrWhiteSpace(expectedHash))
        {
            hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        }

        try
        {
            var buffer = new byte[81920]; // 80KB buffer for faster download
            int bytesRead;
            long downloadedBytes = 0;
            var lastProgress = 0;
            var fileName = Path.GetFileName(destinationPath);

            while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                hasher?.AppendData(buffer.AsSpan(0, bytesRead));

                downloadedBytes += bytesRead;

                // Log progress every 10%
                if (totalBytes > 0)
                {
                    var progress = (int)(downloadedBytes * 100 / totalBytes);
                    if (progress >= lastProgress + 10)
                    {
                        lastProgress = progress;
                        var downloadedMB = downloadedBytes / (1024.0 * 1024.0);
                        var totalMB = totalBytes / (1024.0 * 1024.0);
                        _logger.LogInformation("   📊 {FileName}: {Downloaded:F1} MB / {Total:F1} MB ({Progress}%)",
                            fileName, downloadedMB, totalMB, progress);
                    }
                }
            }

            if (hasher != null)
            {
                var computedHash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                if (!string.Equals(computedHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    await fileStream.DisposeAsync();
                    File.Delete(destinationPath);
                    throw new InvalidOperationException($"Checksum validation failed for {Path.GetFileName(destinationPath)}.");
                }
            }
        }
        finally
        {
            hasher?.Dispose();
        }

        return new FileInfo(destinationPath).Length;
    }

    private static bool ResolveForceFlag(IConfiguration configuration)
    {
        var candidateKeys = new[]
        {
            "ModelDownload:ForceRedownload",
            "ModelDownload:ForceModelRedownload",
            "ForceModelRedownload",
            "force-model-redownload"
        };

        foreach (var key in candidateKeys)
        {
            if (bool.TryParse(configuration[key], out var parsed) && parsed)
            {
                return true;
            }
        }

        if (bool.TryParse(Environment.GetEnvironmentVariable("SENTINAI_FORCE_MODEL_REDOWNLOAD"), out var envFlag) && envFlag)
        {
            return true;
        }

        return false;
    }

    private static string? ExtractSha256(HttpResponseMessage response)
    {
        // Hugging Face does not expose SHA headers consistently. Only trust headers that explicitly
        // identify a SHA-256 hash to avoid false positives from generic ETags.
        var checksumHeaders = new[]
        {
            "x-checksum-sha256",
            "x-raw-sha256",
            "x-amz-meta-checksum-sha256"
        };

        foreach (var headerName in checksumHeaders)
        {
            if (TryGetHeaderValue(response.Headers, headerName, out var headerHash) ||
                TryGetHeaderValue(response.Content.Headers, headerName, out headerHash))
            {
                var normalized = NormalizeSha256(headerHash);
                if (normalized != null)
                {
                    return normalized;
                }
            }
        }

        var etag = response.Headers.ETag?.Tag;
        if (!string.IsNullOrWhiteSpace(etag) &&
            etag.Contains("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            var trimmed = etag.Trim('"');
            if (trimmed.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[2..].Trim('"');
            }

            var markerIndex = trimmed.IndexOf("sha256:", StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                var candidate = trimmed[(markerIndex + 7)..];
                return NormalizeSha256(candidate);
            }
        }

        return null;
    }

    private static bool TryGetHeaderValue(HttpHeaders headers, string headerName, out string? value)
    {
        if (headers.TryGetValues(headerName, out var values))
        {
            value = values.FirstOrDefault();
            return true;
        }

        value = null;
        return false;
    }

    private static string? NormalizeSha256(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var trimmed = rawValue.Trim().Trim('"');
        return trimmed.Length == 64 && trimmed.All(IsHexChar)
            ? trimmed.ToLowerInvariant()
            : null;
    }

    private static bool IsHexChar(char c)
        => (c >= '0' && c <= '9') ||
           (c >= 'a' && c <= 'f') ||
           (c >= 'A' && c <= 'F');

    private void TrackModelEvent(string eventName, IDictionary<string, string>? properties = null, IDictionary<string, double>? metrics = null)
    {
        try
        {
            _telemetry?.TrackEvent(eventName, properties, metrics);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Telemetry event {EventName} failed.", eventName);
        }
    }
}
