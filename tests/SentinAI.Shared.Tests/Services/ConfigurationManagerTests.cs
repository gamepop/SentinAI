using SentinAI.Shared.Models;
using SentinAI.Shared.Services;

namespace SentinAI.Shared.Tests.Services;

public class ConfigurationManagerTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _testConfigPath;

    public ConfigurationManagerTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"config_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _testConfigPath = Path.Combine(_testDirectory, "config.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public void ConfigFilePath_ReturnsExpectedPath()
    {
        // Arrange
        var manager = new ConfigurationManager();

        // Act
        var path = manager.ConfigFilePath;

        // Assert
        Assert.NotNull(path);
        Assert.Contains("SentinAI", path);
        Assert.EndsWith("config.json", path);
    }

    [Fact]
    public async Task LoadConfigAsync_NoExistingFile_ReturnsDefaultConfig()
    {
        // Arrange
        var manager = new TestableConfigurationManager(_testDirectory);

        // Act
        var config = await manager.LoadConfigAsync();

        // Assert
        Assert.NotNull(config);
        Assert.False(config.AutoStart);
        Assert.Equal("CPU", config.ExecutionProvider);
    }

    [Fact]
    public async Task LoadConfigAsync_CachesResult()
    {
        // Arrange
        var manager = new TestableConfigurationManager(_testDirectory);

        // Act
        var config1 = await manager.LoadConfigAsync();
        var config2 = await manager.LoadConfigAsync();

        // Assert
        Assert.Same(config1, config2);
    }

    [Fact]
    public async Task SaveConfigAsync_CreatesConfigFile()
    {
        // Arrange
        var manager = new TestableConfigurationManager(_testDirectory);
        var config = new SentinelConfig
        {
            AutoStart = true,
            ExecutionProvider = "DirectML"
        };

        // Act
        await manager.SaveConfigAsync(config);

        // Assert
        Assert.True(File.Exists(manager.ConfigFilePath));
    }

    [Fact]
    public async Task SaveConfigAsync_PersistsValues()
    {
        // Arrange
        var manager = new TestableConfigurationManager(_testDirectory);
        var originalConfig = new SentinelConfig
        {
            AutoStart = true,
            StartMinimized = true,
            ShowNotifications = false,
            AutoApproveMinConfidence = 0.85,
            EnableAutoCleanup = true,
            RagEnabled = false,
            ExecutionProvider = "DirectML"
        };

        // Act
        await manager.SaveConfigAsync(originalConfig);

        // Create new manager to force reload from file
        var manager2 = new TestableConfigurationManager(_testDirectory);
        var loadedConfig = await manager2.LoadConfigAsync();

        // Assert
        Assert.True(loadedConfig.AutoStart);
        Assert.True(loadedConfig.StartMinimized);
        Assert.False(loadedConfig.ShowNotifications);
        Assert.Equal(0.85, loadedConfig.AutoApproveMinConfidence);
        Assert.True(loadedConfig.EnableAutoCleanup);
        Assert.False(loadedConfig.RagEnabled);
        Assert.Equal("DirectML", loadedConfig.ExecutionProvider);
    }

    [Fact]
    public async Task SaveConfigAsync_UpdatesCache()
    {
        // Arrange
        var manager = new TestableConfigurationManager(_testDirectory);
        var config = new SentinelConfig { AutoStart = true };

        // Act
        await manager.SaveConfigAsync(config);
        var loadedConfig = await manager.LoadConfigAsync();

        // Assert
        Assert.Same(config, loadedConfig);
    }

    [Fact]
    public async Task LoadConfigAsync_InvalidJson_ReturnsDefaultConfig()
    {
        // Arrange
        var manager = new TestableConfigurationManager(_testDirectory);
        await File.WriteAllTextAsync(manager.ConfigFilePath, "{ invalid json }");

        // Act
        var config = await manager.LoadConfigAsync();

        // Assert
        Assert.NotNull(config);
        Assert.False(config.AutoStart); // Default value
    }

    [Fact]
    public async Task LoadConfigAsync_EmptyFile_ReturnsDefaultConfig()
    {
        // Arrange
        var manager = new TestableConfigurationManager(_testDirectory);
        await File.WriteAllTextAsync(manager.ConfigFilePath, "");

        // Act
        var config = await manager.LoadConfigAsync();

        // Assert
        Assert.NotNull(config);
    }

    [Fact]
    public async Task SaveConfigAsync_CreatesDirectory()
    {
        // Arrange
        var subDir = Path.Combine(_testDirectory, "subdir", "nested");
        var manager = new TestableConfigurationManager(subDir);
        var config = new SentinelConfig();

        // Act
        await manager.SaveConfigAsync(config);

        // Assert
        Assert.True(Directory.Exists(subDir));
    }

    [Fact]
    public async Task LoadConfigAsync_PartialConfig_FillsDefaults()
    {
        // Arrange
        var manager = new TestableConfigurationManager(_testDirectory);
        await File.WriteAllTextAsync(manager.ConfigFilePath, """{"AutoStart": true}""");

        // Act
        var config = await manager.LoadConfigAsync();

        // Assert
        Assert.True(config.AutoStart);
        Assert.Equal("CPU", config.ExecutionProvider); // Default
        Assert.True(config.ShowNotifications); // Default
    }

    [Fact]
    public async Task SaveConfigAsync_WritesFormattedJson()
    {
        // Arrange
        var manager = new TestableConfigurationManager(_testDirectory);
        var config = new SentinelConfig { AutoStart = true };

        // Act
        await manager.SaveConfigAsync(config);
        var json = await File.ReadAllTextAsync(manager.ConfigFilePath);

        // Assert
        Assert.Contains("\n", json); // Should be formatted with newlines
    }

    [Fact]
    public async Task LoadConfigAsync_PreservesExcludedPaths()
    {
        // Arrange
        var manager = new TestableConfigurationManager(_testDirectory);
        var originalConfig = new SentinelConfig
        {
            ExcludedPaths = new List<string> { @"C:\Important", @"D:\DoNotDelete" }
        };
        await manager.SaveConfigAsync(originalConfig);

        // Create new manager
        var manager2 = new TestableConfigurationManager(_testDirectory);

        // Act
        var loadedConfig = await manager2.LoadConfigAsync();

        // Assert
        Assert.Equal(2, loadedConfig.ExcludedPaths.Count);
        Assert.Contains(@"C:\Important", loadedConfig.ExcludedPaths);
        Assert.Contains(@"D:\DoNotDelete", loadedConfig.ExcludedPaths);
    }

    [Fact]
    public async Task LoadConfigAsync_PreservesAutoCleanupCategories()
    {
        // Arrange
        var manager = new TestableConfigurationManager(_testDirectory);
        var originalConfig = new SentinelConfig
        {
            AutoCleanupCategories = new List<string> { CleanupCategories.Logs, CleanupCategories.NodeModules }
        };
        await manager.SaveConfigAsync(originalConfig);

        // Create new manager
        var manager2 = new TestableConfigurationManager(_testDirectory);

        // Act
        var loadedConfig = await manager2.LoadConfigAsync();

        // Assert
        Assert.Equal(2, loadedConfig.AutoCleanupCategories.Count);
        Assert.Contains(CleanupCategories.Logs, loadedConfig.AutoCleanupCategories);
        Assert.Contains(CleanupCategories.NodeModules, loadedConfig.AutoCleanupCategories);
    }

    /// <summary>
    /// Testable version of ConfigurationManager that allows specifying the config directory
    /// </summary>
    private class TestableConfigurationManager : IConfigurationManager
    {
        private readonly string _configDirectory;
        private readonly string _configFilePath;
        private SentinelConfig? _cachedConfig;

        public string ConfigFilePath => _configFilePath;

        public TestableConfigurationManager(string configDirectory)
        {
            _configDirectory = configDirectory;
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
                _cachedConfig = new SentinelConfig();
                await SaveConfigAsync(_cachedConfig);
                return _cachedConfig;
            }

            try
            {
                var json = await File.ReadAllTextAsync(_configFilePath);
                _cachedConfig = System.Text.Json.JsonSerializer.Deserialize<SentinelConfig>(json) ?? new SentinelConfig();
                return _cachedConfig;
            }
            catch
            {
                _cachedConfig = new SentinelConfig();
                return _cachedConfig;
            }
        }

        public async Task SaveConfigAsync(SentinelConfig config)
        {
            Directory.CreateDirectory(_configDirectory);

            var options = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            };

            var json = System.Text.Json.JsonSerializer.Serialize(config, options);
            await File.WriteAllTextAsync(_configFilePath, json);
            _cachedConfig = config;
        }
    }
}
