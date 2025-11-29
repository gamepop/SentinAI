using System.Text.RegularExpressions;

namespace SentinAI.Web.Services;

public interface IWinapp2Parser
{
    Task LoadAsync(string winapp2Path);
    bool IsSafeToDelete(string filePath);
    string? GetCleanupRule(string filePath);
}

/// <summary>
/// Parses Winapp2.ini database to ground AI decisions in proven cleanup rules
/// Winapp2.ini is the community-maintained database used by CCleaner and BleachBit
/// </summary>
public class Winapp2Parser : IWinapp2Parser
{
    private readonly Dictionary<string, CleanupRule> _rules = new();
    private bool _isLoaded;

    public async Task LoadAsync(string winapp2Path)
    {
        if (!File.Exists(winapp2Path))
        {
            // Try to download from official source
            await DownloadWinapp2Async(winapp2Path);
        }

        var lines = await File.ReadAllLinesAsync(winapp2Path);
        ParseWinapp2(lines);
        _isLoaded = true;
    }

    private void ParseWinapp2(string[] lines)
    {
        CleanupRule? currentRule = null;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // Skip comments and empty lines
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(';'))
                continue;

            // New section starts
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                if (currentRule != null)
                {
                    _rules[currentRule.Name] = currentRule;
                }

                var sectionName = trimmed.Trim('[', ']');
                currentRule = new CleanupRule { Name = sectionName };
                continue;
            }

            if (currentRule == null) continue;

            // Parse key=value pairs
            var parts = trimmed.Split('=', 2);
            if (parts.Length != 2) continue;

            var key = parts[0].Trim();
            var value = parts[1].Trim();

            if (key.StartsWith("FileKey", StringComparison.OrdinalIgnoreCase))
            {
                currentRule.FilePaths.Add(ParseFileKey(value));
            }
            else if (key.Equals("Section", StringComparison.OrdinalIgnoreCase))
            {
                currentRule.Category = value;
            }
        }

        // Add last rule
        if (currentRule != null)
        {
            _rules[currentRule.Name] = currentRule;
        }
    }

    private string ParseFileKey(string fileKey)
    {
        // Winapp2 uses special syntax like %ProgramFiles%|*.*|RECURSE
        // Convert to standard path pattern
        var cleanedPath = fileKey
            .Replace("%ProgramFiles%", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
            .Replace("%ProgramData%", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData))
            .Replace("%LocalAppData%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .Replace("%AppData%", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
            .Replace("%Temp%", Path.GetTempPath())
            .Replace("%WinDir%", Environment.GetFolderPath(Environment.SpecialFolder.Windows))
            .Split('|')[0]; // Take just the path part

        return cleanedPath;
    }

    public bool IsSafeToDelete(string filePath)
    {
        if (!_isLoaded) return false;

        foreach (var rule in _rules.Values)
        {
            foreach (var pattern in rule.FilePaths)
            {
                if (IsMatch(filePath, pattern))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public string? GetCleanupRule(string filePath)
    {
        if (!_isLoaded) return null;

        foreach (var rule in _rules.Values)
        {
            foreach (var pattern in rule.FilePaths)
            {
                if (IsMatch(filePath, pattern))
                {
                    return $"{rule.Name} ({rule.Category})";
                }
            }
        }

        return null;
    }

    private bool IsMatch(string filePath, string pattern)
    {
        // Simple wildcard matching
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";

        return Regex.IsMatch(filePath, regexPattern, RegexOptions.IgnoreCase);
    }

    private async Task DownloadWinapp2Async(string targetPath)
    {
        const string WINAPP2_URL = "https://raw.githubusercontent.com/MoscaDotTo/Winapp2/master/Winapp2.ini";

        using var httpClient = new HttpClient();
        var content = await httpClient.GetStringAsync(WINAPP2_URL);

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await File.WriteAllTextAsync(targetPath, content);
    }

    private class CleanupRule
    {
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public List<string> FilePaths { get; set; } = new();
    }
}
