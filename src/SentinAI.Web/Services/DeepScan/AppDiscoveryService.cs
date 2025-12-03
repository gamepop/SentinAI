using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SentinAI.Shared.Models.DeepScan;

namespace SentinAI.Web.Services.DeepScan;

/// <summary>
/// Service for discovering installed applications on the system.
/// </summary>
[SupportedOSPlatform("windows")]
public class AppDiscoveryService
{
    private readonly ILogger<AppDiscoveryService> _logger;

    public AppDiscoveryService(ILogger<AppDiscoveryService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Discovers all installed applications on the system.
    /// </summary>
    public async Task<List<InstalledApp>> DiscoverAppsAsync(CancellationToken ct = default)
    {
        var apps = new List<InstalledApp>();

        await Task.Run(() =>
        {
            // Discover from Registry (traditional desktop apps)
            apps.AddRange(DiscoverFromRegistry(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"));
            apps.AddRange(DiscoverFromRegistry(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"));

            // Discover Store apps
            apps.AddRange(DiscoverStoreApps());
        }, ct);

        _logger.LogInformation("Discovered {Count} installed applications", apps.Count);
        return apps;
    }

    private List<InstalledApp> DiscoverFromRegistry(string registryPath)
    {
        var apps = new List<InstalledApp>();

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(registryPath);
            if (key == null) return apps;

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                try
                {
                    using var subKey = key.OpenSubKey(subKeyName);
                    if (subKey == null) continue;

                    var displayName = subKey.GetValue("DisplayName")?.ToString();
                    if (string.IsNullOrWhiteSpace(displayName)) continue;

                    // Skip system components
                    var systemComponent = subKey.GetValue("SystemComponent");
                    if (systemComponent != null && (int)systemComponent == 1) continue;

                    var app = new InstalledApp
                    {
                        Id = subKeyName,
                        Name = displayName,
                        Publisher = subKey.GetValue("Publisher")?.ToString() ?? "Unknown",
                        Version = subKey.GetValue("DisplayVersion")?.ToString(),
                        InstallLocation = subKey.GetValue("InstallLocation")?.ToString(),
                        Source = AppSource.Desktop,
                        CanUninstall = !string.IsNullOrEmpty(subKey.GetValue("UninstallString")?.ToString())
                    };

                    // Parse install date
                    var installDateStr = subKey.GetValue("InstallDate")?.ToString();
                    if (!string.IsNullOrEmpty(installDateStr) && DateTime.TryParseExact(installDateStr, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var installDate))
                    {
                        app.InstallDate = installDate;
                    }

                    // Get size
                    var estimatedSize = subKey.GetValue("EstimatedSize");
                    if (estimatedSize != null)
                    {
                        app.InstallSizeBytes = Convert.ToInt64(estimatedSize) * 1024; // KB to bytes
                    }

                    // Detect bloatware
                    app.IsBloatware = IsBloatware(app);

                    // Categorize
                    app.Category = CategorizeApp(app);

                    apps.Add(app);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error reading registry key {Key}", subKeyName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading registry path {Path}", registryPath);
        }

        return apps;
    }

    private List<InstalledApp> DiscoverStoreApps()
    {
        var apps = new List<InstalledApp>();

        try
        {
            // Use PowerShell to get Store apps (safer than COM interop)
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"Get-AppxPackage | Select-Object Name, Publisher, InstallLocation, Version | ConvertTo-Json\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null) return apps;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            if (!string.IsNullOrWhiteSpace(output))
            {
                // Parse JSON output
                var packages = System.Text.Json.JsonSerializer.Deserialize<List<StoreAppInfo>>(output);
                if (packages != null)
                {
                    foreach (var pkg in packages)
                    {
                        if (string.IsNullOrWhiteSpace(pkg.Name)) continue;

                        // Skip framework packages
                        if (pkg.Name.Contains(".NET") || pkg.Name.Contains("VCLibs") || pkg.Name.Contains("WindowsAppRuntime"))
                            continue;

                        var app = new InstalledApp
                        {
                            Id = pkg.Name ?? "",
                            Name = GetFriendlyAppName(pkg.Name ?? ""),
                            Publisher = CleanPublisher(pkg.Publisher ?? "Unknown"),
                            Version = pkg.Version,
                            InstallLocation = pkg.InstallLocation,
                            Source = AppSource.MicrosoftStore,
                            CanUninstall = !IsSystemStoreApp(pkg.Name ?? "")
                        };

                        // Get size from install location
                        if (!string.IsNullOrEmpty(pkg.InstallLocation) && Directory.Exists(pkg.InstallLocation))
                        {
                            try
                            {
                                var dirInfo = new DirectoryInfo(pkg.InstallLocation);
                                app.InstallSizeBytes = GetDirectorySize(dirInfo);
                            }
                            catch { }
                        }

                        app.IsBloatware = IsBloatware(app);
                        app.Category = CategorizeApp(app);
                        app.IsSystemApp = IsSystemStoreApp(pkg.Name ?? "");

                        apps.Add(app);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error discovering Store apps");
        }

        return apps;
    }

    private static long GetDirectorySize(DirectoryInfo dir)
    {
        long size = 0;
        try
        {
            foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try { size += file.Length; } catch { }
            }
        }
        catch { }
        return size;
    }

    private static string GetFriendlyAppName(string packageName)
    {
        // Convert package names like "Microsoft.WindowsCalculator" to "Windows Calculator"
        var parts = packageName.Split('.');
        if (parts.Length > 1)
        {
            var name = parts.Last();
            // Add spaces before capitals
            return System.Text.RegularExpressions.Regex.Replace(name, "([a-z])([A-Z])", "$1 $2");
        }
        return packageName;
    }

    private static string CleanPublisher(string publisher)
    {
        // Clean up publisher strings like "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond..."
        if (publisher.StartsWith("CN="))
        {
            var parts = publisher.Split(',');
            return parts[0].Replace("CN=", "").Trim();
        }
        return publisher;
    }

    private static bool IsSystemStoreApp(string name)
    {
        var systemApps = new[]
        {
            "Microsoft.WindowsStore",
            "Microsoft.Windows.Photos",
            "Microsoft.WindowsCamera",
            "Microsoft.WindowsCalculator",
            "Microsoft.WindowsNotepad",
            "Microsoft.Paint",
            "Microsoft.ScreenSketch",
            "windows.immersivecontrolpanel",
            "Microsoft.AAD.BrokerPlugin",
            "Microsoft.AccountsControl",
            "Microsoft.Windows.CloudExperienceHost"
        };

        return systemApps.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsBloatware(InstalledApp app)
    {
        var bloatwareNames = new[]
        {
            "Candy", "Solitaire", "Bubble", "Farm", "Hidden City",
            "Disney", "Spotify", "Netflix", "TikTok", "Facebook",
            "LinkedIn", "Twitter", "Instagram", "Clipchamp",
            "McAfee", "Norton", "ExpressVPN", "Avast"
        };

        return bloatwareNames.Any(b =>
            app.Name.Contains(b, StringComparison.OrdinalIgnoreCase) ||
            app.Publisher.Contains(b, StringComparison.OrdinalIgnoreCase));
    }

    private static AppCategory CategorizeApp(InstalledApp app)
    {
        var name = app.Name.ToLowerInvariant();
        var publisher = app.Publisher.ToLowerInvariant();

        if (name.Contains("game") || name.Contains("xbox") || publisher.Contains("game"))
            return AppCategory.Gaming;
        if (name.Contains("visual studio") || name.Contains("code") || name.Contains("sdk") || name.Contains("git"))
            return AppCategory.Development;
        if (name.Contains("chrome") || name.Contains("firefox") || name.Contains("edge") || name.Contains("browser"))
            return AppCategory.Browser;
        if (name.Contains("office") || name.Contains("word") || name.Contains("excel") || name.Contains("outlook"))
            return AppCategory.Productivity;
        if (name.Contains("vlc") || name.Contains("media") || name.Contains("player") || name.Contains("photo"))
            return AppCategory.Media;
        if (name.Contains("security") || name.Contains("antivirus") || name.Contains("defender"))
            return AppCategory.Security;
        if (name.Contains("discord") || name.Contains("teams") || name.Contains("zoom") || name.Contains("slack"))
            return AppCategory.Communication;

        return AppCategory.Other;
    }

    private class StoreAppInfo
    {
        public string? Name { get; set; }
        public string? Publisher { get; set; }
        public string? InstallLocation { get; set; }
        public string? Version { get; set; }
    }
}
