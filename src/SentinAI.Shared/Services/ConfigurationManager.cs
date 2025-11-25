using System.Text.Json;
using SentinAI.Shared.Models;

namespace SentinAI.Shared.Services;

/// <summary>
/// Manages loading and saving configuration from %LocalAppData%\SentinAI\config.json
/// </summary>
public interface IConfigurationManager
{
    Task<SentinelConfig> LoadConfigAsync();
    Task SaveConfigAsync(SentinelConfig config);
    string ConfigFilePath { get; }
}

public class ConfigurationManager : IConfigurationManager
{
    private readonly string _configDirectory;
    private readonly string _configFilePath;
    private SentinelConfig? _cachedConfig;

    public string ConfigFilePath => _configFilePath;

    public ConfigurationManager()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _configDirectory = Path.Combine(localAppData, "SentinAI");
        _configFilePath = Path.Combine(_configDirectory, "config.json");
    }

    public async Task<SentinelConfig> LoadConfigAsync()
    {
        if (_cachedConfig != null)
        {
            return _cachedConfig;
        }

        if (!File.Exists(_configFilePath))
        {
            // Create default configuration
            _cachedConfig = new SentinelConfig();
            await SaveConfigAsync(_cachedConfig);
            return _cachedConfig;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_configFilePath);
            _cachedConfig = JsonSerializer.Deserialize<SentinelConfig>(json) ?? new SentinelConfig();
            return _cachedConfig;
        }
        catch (Exception)
        {
            // If deserialization fails, return default config
            _cachedConfig = new SentinelConfig();
            return _cachedConfig;
        }
    }

    public async Task SaveConfigAsync(SentinelConfig config)
    {
        Directory.CreateDirectory(_configDirectory);

        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        var json = JsonSerializer.Serialize(config, options);
        await File.WriteAllTextAsync(_configFilePath, json);
        _cachedConfig = config;
    }
}
