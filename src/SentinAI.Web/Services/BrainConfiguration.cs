namespace SentinAI.Web.Services;

/// <summary>
/// Configuration for the AI Brain service
/// </summary>
public class BrainConfiguration
{
    public const string SectionName = "Brain";

    /// <summary>
    /// The execution provider to use: "CPU" or "DirectML" (GPU)
    /// CPU is more compatible but slower
    /// DirectML uses GPU acceleration but requires DirectX 12 support
    /// </summary>
    public string ExecutionProvider { get; set; } = "CPU";

    /// <summary>
    /// Custom model path. If empty, uses default location in LocalAppData
    /// </summary>
    public string ModelPath { get; set; } = "";

    /// <summary>
    /// Force re-download of the model even if it exists
    /// </summary>
    public bool ForceModelRedownload { get; set; } = false;

    /// <summary>
    /// Maximum time in seconds to wait for AI inference
    /// </summary>
    public int InferenceTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum total sequence length (input tokens + output tokens)
    /// Phi-4 Mini supports up to 128k tokens context window
    /// </summary>
    public int MaxSequenceLength { get; set; } = 4096;

    /// <summary>
    /// Maximum tokens to generate in the output response
    /// Keep small for fast inference on cleanup decisions
    /// </summary>
    public int MaxOutputTokens { get; set; } = 150;

    /// <summary>
    /// Temperature for model inference (0.0-1.0, lower = more deterministic)
    /// </summary>
    public double Temperature { get; set; } = 0.1;

    /// <summary>
    /// Whether to use CPU execution provider
    /// </summary>
    public bool UseCpu => ExecutionProvider.Equals("CPU", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether to use DirectML (GPU) execution provider
    /// </summary>
    public bool UseDirectML => ExecutionProvider.Equals("DirectML", StringComparison.OrdinalIgnoreCase) ||
                               ExecutionProvider.Equals("GPU", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the HuggingFace model subdirectory based on execution provider
    /// </summary>
    public string GetModelSubdirectory()
    {
        return UseDirectML
            ? "gpu/gpu-int4-rtn-block-32"  // GPU/DirectML version
            : "cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4";  // CPU version
    }

    /// <summary>
    /// Gets the model ONNX file names (same for CPU and DirectML in Phi-4)
    /// </summary>
    public (string OnnxFile, string OnnxDataFile) GetModelFileNames()
    {
        return ("model.onnx", "model.onnx.data");
    }

    /// <summary>
    /// Gets a display name for the current execution provider
    /// </summary>
    public string GetProviderDisplayName()
    {
        return UseDirectML ? "DirectML (GPU)" : "CPU";
    }
}
