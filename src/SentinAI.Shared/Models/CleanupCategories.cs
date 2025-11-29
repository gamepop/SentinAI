namespace SentinAI.Shared.Models;

/// <summary>
/// Predefined categories for cleanup items
/// </summary>
public static class CleanupCategories
{
    /// <summary>
    /// Temporary files (Windows Temp, User Temp)
    /// </summary>
    public const string Temp = "Temp";

    /// <summary>
    /// Cache files (browser cache, application cache)
    /// </summary>
    public const string Cache = "Cache";

    /// <summary>
    /// Downloaded files
    /// </summary>
    public const string Downloads = "Downloads";

    /// <summary>
    /// Node.js modules (node_modules folders)
    /// </summary>
    public const string NodeModules = "NodeModules";

    /// <summary>
    /// Build artifacts (.NET bin/obj, etc.)
    /// </summary>
    public const string BuildArtifacts = "BuildArtifacts";

    /// <summary>
    /// Log files
    /// </summary>
    public const string Logs = "Logs";

    /// <summary>
    /// Unknown or uncategorized items
    /// </summary>
    public const string Unknown = "Unknown";
}
