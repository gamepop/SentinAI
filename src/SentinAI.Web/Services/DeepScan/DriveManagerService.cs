using Microsoft.Extensions.Logging;
using SentinAI.Shared.Models.DeepScan;

namespace SentinAI.Web.Services.DeepScan;

/// <summary>
/// Manages drive information and space analysis.
/// </summary>
public class DriveManagerService
{
    private readonly ILogger<DriveManagerService> _logger;
    
    public DriveManagerService(ILogger<DriveManagerService> logger)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// Gets all available drives.
    /// </summary>
    public List<TargetDriveInfo> GetAvailableDrives()
    {
        return System.IO.DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == System.IO.DriveType.Fixed)
            .Select(d => new TargetDriveInfo
            {
                Letter = d.Name,
                Label = d.VolumeLabel,
                FreeBytes = d.AvailableFreeSpace,
                TotalBytes = d.TotalSize
            })
            .ToList();
    }
    
    /// <summary>
    /// Analyzes a drive for space usage.
    /// </summary>
    public async Task<DriveAnalysis> AnalyzeDriveAsync(string drivePath, CancellationToken ct = default)
    {
        var driveInfo = new System.IO.DriveInfo(drivePath.TrimEnd('\\'));
        
        var analysis = new DriveAnalysis
        {
            DriveLetter = driveInfo.Name,
            VolumeLabel = driveInfo.VolumeLabel,
            DriveType = (DriveType)(int)driveInfo.DriveType,
            TotalBytes = driveInfo.TotalSize,
            FreeBytes = driveInfo.AvailableFreeSpace
        };
        
        _logger.LogInformation(
            "Analyzing drive {Drive}: {Total} total, {Free} free ({Used}% used)",
            drivePath, analysis.TotalFormatted, analysis.FreeFormatted, analysis.UsedPercentage.ToString("F1"));
        
        // Analyze categories
        await Task.Run(() =>
        {
            analysis.Categories = AnalyzeCategories(drivePath, ct);
        }, ct);
        
        // Find largest directories
        analysis.LargestDirectories = await FindLargestDirectoriesAsync(drivePath, 20, ct);
        
        return analysis;
    }
    
    private List<SpaceCategory> AnalyzeCategories(string drivePath, CancellationToken ct)
    {
        var categories = new List<SpaceCategory>();
        var root = drivePath.TrimEnd('\\') + "\\";
        
        // System files
        var windowsPath = Path.Combine(root, "Windows");
        if (Directory.Exists(windowsPath))
        {
            categories.Add(new SpaceCategory
            {
                Name = "Windows System",
                Type = SpaceCategoryType.System,
                Bytes = GetDirectorySizeFast(windowsPath, ct),
                Color = "#4285f4"
            });
        }
        
        // Program Files
        var programFiles = Path.Combine(root, "Program Files");
        var programFilesX86 = Path.Combine(root, "Program Files (x86)");
        var appBytes = 0L;
        
        if (Directory.Exists(programFiles))
            appBytes += GetDirectorySizeFast(programFiles, ct);
        if (Directory.Exists(programFilesX86))
            appBytes += GetDirectorySizeFast(programFilesX86, ct);
        
        if (appBytes > 0)
        {
            categories.Add(new SpaceCategory
            {
                Name = "Installed Applications",
                Type = SpaceCategoryType.Applications,
                Bytes = appBytes,
                Color = "#34a853"
            });
        }
        
        // User folders
        var usersPath = Path.Combine(root, "Users");
        if (Directory.Exists(usersPath))
        {
            foreach (var userDir in Directory.GetDirectories(usersPath))
            {
                ct.ThrowIfCancellationRequested();
                
                var userName = Path.GetFileName(userDir);
                if (userName is "Public" or "Default" or "Default User" or "All Users")
                    continue;
                
                // Documents
                var docsPath = Path.Combine(userDir, "Documents");
                if (Directory.Exists(docsPath))
                {
                    categories.Add(new SpaceCategory
                    {
                        Name = $"Documents ({userName})",
                        Type = SpaceCategoryType.UserDocuments,
                        Bytes = GetDirectorySizeFast(docsPath, ct),
                        Color = "#fbbc04"
                    });
                }
                
                // Downloads
                var downloadsPath = Path.Combine(userDir, "Downloads");
                if (Directory.Exists(downloadsPath))
                {
                    categories.Add(new SpaceCategory
                    {
                        Name = $"Downloads ({userName})",
                        Type = SpaceCategoryType.UserDownloads,
                        Bytes = GetDirectorySizeFast(downloadsPath, ct),
                        Color = "#ea4335"
                    });
                }
                
                // AppData Local
                var appDataLocal = Path.Combine(userDir, "AppData", "Local");
                if (Directory.Exists(appDataLocal))
                {
                    categories.Add(new SpaceCategory
                    {
                        Name = $"Local App Data ({userName})",
                        Type = SpaceCategoryType.AppDataLocal,
                        Bytes = GetDirectorySizeFast(appDataLocal, ct),
                        Color = "#9c27b0"
                    });
                }
                
                // Temp
                var tempPath = Path.Combine(appDataLocal, "Temp");
                if (Directory.Exists(tempPath))
                {
                    categories.Add(new SpaceCategory
                    {
                        Name = $"Temp Files ({userName})",
                        Type = SpaceCategoryType.TempFiles,
                        Bytes = GetDirectorySizeFast(tempPath, ct),
                        Color = "#ff9800"
                    });
                }
            }
        }
        
        // Windows Temp
        var winTemp = Path.Combine(root, "Windows", "Temp");
        if (Directory.Exists(winTemp))
        {
            categories.Add(new SpaceCategory
            {
                Name = "Windows Temp",
                Type = SpaceCategoryType.TempFiles,
                Bytes = GetDirectorySizeFast(winTemp, ct),
                Color = "#ff5722"
            });
        }
        
        // Calculate percentages
        var totalCategorized = categories.Sum(c => c.Bytes);
        foreach (var cat in categories)
        {
            cat.Percentage = totalCategorized > 0 ? (cat.Bytes * 100.0 / totalCategorized) : 0;
        }
        
        return categories.OrderByDescending(c => c.Bytes).ToList();
    }
    
    private long GetDirectorySizeFast(string path, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            
            var size = 0L;
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            
            foreach (var file in Directory.EnumerateFiles(path, "*", options))
            {
                try
                {
                    size += new FileInfo(file).Length;
                }
                catch { }
            }
            
            return size;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Error getting size for {Path}", path);
            return 0;
        }
    }
    
    private async Task<List<DirectorySize>> FindLargestDirectoriesAsync(
        string drivePath, 
        int count, 
        CancellationToken ct)
    {
        var results = new List<DirectorySize>();
        
        await Task.Run(() =>
        {
            var root = drivePath.TrimEnd('\\') + "\\";
            var scannedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            // Scan first-level directories
            try
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    ct.ThrowIfCancellationRequested();
                    
                    var name = Path.GetFileName(dir);
                    if (name is "$Recycle.Bin" or "System Volume Information" or "$WinREAgent")
                        continue;
                    
                    try
                    {
                        var size = GetDirectorySizeFast(dir, ct);
                        if (size > 100 * 1024 * 1024) // >100MB
                        {
                            results.Add(new DirectorySize
                            {
                                Path = dir,
                                Name = name,
                                Bytes = size,
                                LastModified = Directory.GetLastWriteTime(dir)
                            });
                        }
                    }
                    catch { }
                }
                
                // Also scan some key subdirectories
                var keyPaths = new[]
                {
                    Path.Combine(root, "Users"),
                    Path.Combine(root, "Program Files"),
                    Path.Combine(root, "Program Files (x86)")
                };
                
                foreach (var keyPath in keyPaths)
                {
                    if (!Directory.Exists(keyPath)) continue;
                    
                    foreach (var dir in Directory.GetDirectories(keyPath))
                    {
                        ct.ThrowIfCancellationRequested();
                        
                        if (scannedPaths.Contains(dir)) continue;
                        scannedPaths.Add(dir);
                        
                        try
                        {
                            var size = GetDirectorySizeFast(dir, ct);
                            if (size > 500 * 1024 * 1024) // >500MB
                            {
                                results.Add(new DirectorySize
                                {
                                    Path = dir,
                                    Name = Path.GetFileName(dir),
                                    Bytes = size,
                                    LastModified = Directory.GetLastWriteTime(dir)
                                });
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error scanning directories in {Path}", drivePath);
            }
        }, ct);
        
        return results
            .OrderByDescending(d => d.Bytes)
            .Take(count)
            .ToList();
    }
    
    /// <summary>
    /// Checks if a drive can accept files of the specified size.
    /// </summary>
    public bool CanRelocateTo(string targetDrive, long requiredBytes)
    {
        try
        {
            var driveInfo = new System.IO.DriveInfo(targetDrive.TrimEnd('\\'));
            // Require at least 10% headroom plus the required space
            var headroom = driveInfo.TotalSize * 0.1;
            return driveInfo.AvailableFreeSpace > (requiredBytes + headroom);
        }
        catch
        {
            return false;
        }
    }
    
    /// <summary>
    /// Gets drives that can accept the specified file size.
    /// </summary>
    public List<TargetDriveInfo> GetRelocationTargets(long requiredBytes, string excludeDrive)
    {
        return GetAvailableDrives()
            .Where(d => !d.Letter.StartsWith(excludeDrive, StringComparison.OrdinalIgnoreCase))
            .Where(d => d.FreeBytes > requiredBytes * 1.2) // 20% headroom
            .OrderByDescending(d => d.FreeBytes)
            .ToList();
    }
}
