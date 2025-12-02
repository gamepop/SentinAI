using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SentinAI.Shared.Models.DeepScan;

namespace SentinAI.Web.Services.DeepScan;

/// <summary>
/// Discovers installed applications from various sources.
/// </summary>
public class AppDiscoveryService
{
    private readonly ILogger<AppDiscoveryService> _logger;
    
    public AppDiscoveryService(ILogger<AppDiscoveryService> logger)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// Discovers all installed applications asynchronously.
    /// </summary>
    public async IAsyncEnumerable<InstalledApp> DiscoverAppsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        _logger.LogInformation("Starting application discovery");
        
        // Discover from Registry (traditional installers)
        await foreach (var app in DiscoverFromRegistryAsync(ct))
        {
            yield return app;
        }
        
        // Discover Microsoft Store apps
        await foreach (var app in DiscoverStoreAppsAsync(ct))
        {
            yield return app;
        }
        
        // Discover Steam games
        await foreach (var app in DiscoverSteamGamesAsync(ct))
        {
            yield return app;
        }
    }
    
    private async IAsyncEnumerable<InstalledApp> DiscoverFromRegistryAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var registryPaths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };
        
        foreach (var basePath in registryPaths)
        {
            ct.ThrowIfCancellationRequested();
            
            using var key = Registry.LocalMachine.OpenSubKey(basePath);
            if (key == null) continue;
            
            foreach (var subKeyName in key.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                
                using var subKey = key.OpenSubKey(subKeyName);
                if (subKey == null) continue;
                
                var displayName = subKey.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(displayName)) continue;
                
                var app = new InstalledApp
                {
                    Id = subKeyName,
                    Name = displayName,
                    Publisher = subKey.GetValue("Publisher") as string ?? "",
                    Version = subKey.GetValue("DisplayVersion") as string ?? "",
                    InstallPath = subKey.GetValue("InstallLocation") as string ?? "",
                    UninstallCommand = subKey.GetValue("UninstallString") as string,
                    RegistryKey = $@"HKLM\{basePath}\{subKeyName}",
                    Source = AppSource.Registry,
                    IsSystemApp = IsSystemApp(displayName, subKey.GetValue("Publisher") as string)
                };
                
                // Parse install date
                var installDateStr = subKey.GetValue("InstallDate") as string;
                if (DateTime.TryParseExact(installDateStr, "yyyyMMdd", null, 
                    System.Globalization.DateTimeStyles.None, out var installDate))
                {
                    app.InstallDate = installDate;
                }
                
                // Get size from registry
                var estimatedSize = subKey.GetValue("EstimatedSize");
                if (estimatedSize is int sizeKb)
                {
                    app.InstallSizeBytes = sizeKb * 1024L;
                }
                
                // Categorize
                CategorizeApp(app);
                
                // Check for bloatware
                app.IsBloatware = IsBloatware(app);
                
                // Calculate actual sizes if install path exists
                if (!string.IsNullOrEmpty(app.InstallPath) && Directory.Exists(app.InstallPath))
                {
                    await Task.Run(() => CalculateAppSizes(app), ct);
                }
                
                yield return app;
            }
        }
    }
    
    private async IAsyncEnumerable<InstalledApp> DiscoverStoreAppsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // Use PowerShell to get Store apps
        var script = "Get-AppxPackage | Select-Object Name, Publisher, Version, InstallLocation, PackageFullName | ConvertTo-Json";
        
        var apps = new List<InstalledApp>();
        
        await Task.Run(() =>
        {
            try
            {
                using var process = new System.Diagnostics.Process();
                process.StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -Command \"{script}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(30000);
                
                if (!string.IsNullOrWhiteSpace(output))
                {
                    var jsonApps = System.Text.Json.JsonSerializer.Deserialize<List<StoreAppInfo>>(output);
                    if (jsonApps != null)
                    {
                        foreach (var storeApp in jsonApps)
                        {
                            if (string.IsNullOrEmpty(storeApp.Name)) continue;
                            
                            var app = new InstalledApp
                            {
                                Id = storeApp.PackageFullName ?? storeApp.Name,
                                Name = GetFriendlyAppName(storeApp.Name),
                                Publisher = storeApp.Publisher ?? "",
                                Version = storeApp.Version ?? "",
                                InstallPath = storeApp.InstallLocation ?? "",
                                Source = AppSource.MicrosoftStore,
                                IsSystemApp = storeApp.Name.StartsWith("Microsoft.Windows")
                            };
                            
                            CategorizeApp(app);
                            app.IsBloatware = IsBloatware(app);
                            
                            apps.Add(app);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enumerate Store apps");
            }
        }, ct);
        
        foreach (var app in apps)
        {
            yield return app;
        }
    }
    
    private async IAsyncEnumerable<InstalledApp> DiscoverSteamGamesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var steamPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            @"C:\Steam",
            @"D:\Steam",
            @"D:\SteamLibrary"
        };
        
        foreach (var steamPath in steamPaths)
        {
            ct.ThrowIfCancellationRequested();
            
            var steamAppsPath = Path.Combine(steamPath, "steamapps");
            if (!Directory.Exists(steamAppsPath)) continue;
            
            var manifestFiles = Directory.GetFiles(steamAppsPath, "appmanifest_*.acf");
            
            foreach (var manifest in manifestFiles)
            {
                ct.ThrowIfCancellationRequested();
                
                var app = await ParseSteamManifestAsync(manifest, steamAppsPath, ct);
                if (app != null)
                {
                    yield return app;
                }
            }
        }
    }
    
    private async Task<InstalledApp?> ParseSteamManifestAsync(
        string manifestPath, 
        string steamAppsPath,
        CancellationToken ct)
    {
        try
        {
            var content = await File.ReadAllTextAsync(manifestPath, ct);
            
            // Simple ACF parser
            var name = ExtractAcfValue(content, "name");
            var installDir = ExtractAcfValue(content, "installdir");
            var appId = ExtractAcfValue(content, "appid");
            var sizeOnDisk = ExtractAcfValue(content, "SizeOnDisk");
            
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(installDir))
                return null;
            
            var fullPath = Path.Combine(steamAppsPath, "common", installDir);
            
            var app = new InstalledApp
            {
                Id = $"steam_{appId}",
                Name = name,
                Publisher = "Steam",
                InstallPath = fullPath,
                Source = AppSource.Steam,
                Category = AppCategory.Gaming,
                CanRelocate = true
            };
            
            if (long.TryParse(sizeOnDisk, out var size))
            {
                app.InstallSizeBytes = size;
            }
            
            // Get last accessed
            if (Directory.Exists(fullPath))
            {
                app.LastAccessed = new DirectoryInfo(fullPath).LastAccessTime;
            }
            
            return app;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse Steam manifest: {Path}", manifestPath);
            return null;
        }
    }
    
    private string? ExtractAcfValue(string content, string key)
    {
        var pattern = $"\"{key}\"\\s+\"([^\"]+)\"";
        var match = System.Text.RegularExpressions.Regex.Match(content, pattern, 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
    
    private void CalculateAppSizes(InstalledApp app)
    {
        try
        {
            if (Directory.Exists(app.InstallPath))
            {
                app.InstallSizeBytes = GetDirectorySize(app.InstallPath);
                app.LastAccessed = new DirectoryInfo(app.InstallPath).LastAccessTime;
            }
            
            // Find associated AppData folders
            var appDataLocal = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appDataRoaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            
            // Common patterns for app data
            var possibleNames = new[] { app.Name, app.Publisher, app.Name.Replace(" ", "") };
            
            foreach (var name in possibleNames.Where(n => !string.IsNullOrEmpty(n)))
            {
                var localPath = Path.Combine(appDataLocal, name);
                if (Directory.Exists(localPath))
                {
                    app.DataFolders.Add(localPath);
                    app.DataSizeBytes += GetDirectorySize(localPath);
                }
                
                var roamingPath = Path.Combine(appDataRoaming, name);
                if (Directory.Exists(roamingPath))
                {
                    app.DataFolders.Add(roamingPath);
                    app.DataSizeBytes += GetDirectorySize(roamingPath);
                }
                
                // Cache folders
                var cachePath = Path.Combine(appDataLocal, name, "Cache");
                if (Directory.Exists(cachePath))
                {
                    app.CacheFolders.Add(cachePath);
                    app.CacheSizeBytes += GetDirectorySize(cachePath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error calculating sizes for {App}", app.Name);
        }
    }
    
    private long GetDirectorySize(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true
            }).Sum(f => new FileInfo(f).Length);
        }
        catch
        {
            return 0;
        }
    }
    
    private void CategorizeApp(InstalledApp app)
    {
        var name = app.Name.ToLowerInvariant();
        var publisher = app.Publisher.ToLowerInvariant();
        
        if (name.Contains("game") || publisher.Contains("games") || app.Source == AppSource.Steam)
            app.Category = AppCategory.Gaming;
        else if (name.Contains("visual studio") || name.Contains("vscode") || name.Contains("jetbrains"))
            app.Category = AppCategory.Development;
        else if (name.Contains("chrome") || name.Contains("firefox") || name.Contains("edge") || name.Contains("browser"))
            app.Category = AppCategory.Browser;
        else if (name.Contains("office") || name.Contains("word") || name.Contains("excel"))
            app.Category = AppCategory.Productivity;
        else if (name.Contains("discord") || name.Contains("slack") || name.Contains("teams") || name.Contains("zoom"))
            app.Category = AppCategory.Communication;
        else if (name.Contains("vlc") || name.Contains("spotify") || name.Contains("itunes"))
            app.Category = AppCategory.Media;
        else if (name.Contains("antivirus") || name.Contains("security") || name.Contains("defender"))
            app.Category = AppCategory.Security;
        else if (publisher.Contains("microsoft") && (name.Contains("windows") || name.Contains(".net")))
            app.Category = AppCategory.System;
        else
            app.Category = AppCategory.Unknown;
    }
    
    private bool IsBloatware(InstalledApp app)
    {
        foreach (var pattern in BloatwarePatterns.KnownBloatwarePatterns)
        {
            if (app.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        
        foreach (var publisher in BloatwarePatterns.KnownBloatwarePublishers)
        {
            if (app.Publisher.Contains(publisher, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        
        return false;
    }
    
    private bool IsSystemApp(string name, string? publisher)
    {
        if (string.IsNullOrEmpty(name)) return false;
        
        foreach (var essential in BloatwarePatterns.EssentialApps)
        {
            if (name.Contains(essential, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        
        if (publisher?.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (name.Contains("Windows", StringComparison.OrdinalIgnoreCase) ||
                name.Contains(".NET", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Visual C++", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        
        return false;
    }
    
    private string GetFriendlyAppName(string packageName)
    {
        // Convert package names like "Microsoft.WindowsCalculator" to "Windows Calculator"
        var parts = packageName.Split('.');
        if (parts.Length > 1)
        {
            var name = parts[^1];
            // Add spaces before capital letters
            return System.Text.RegularExpressions.Regex.Replace(name, "([a-z])([A-Z])", "$1 $2");
        }
        return packageName;
    }
    
    private class StoreAppInfo
    {
        public string? Name { get; set; }
        public string? Publisher { get; set; }
        public string? Version { get; set; }
        public string? InstallLocation { get; set; }
        public string? PackageFullName { get; set; }
    }
}
