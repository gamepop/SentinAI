using Microsoft.Extensions.Logging;
using SentinAI.Shared.Models.DeepScan;

namespace SentinAI.Web.Services.DeepScan;

/// <summary>
/// Analyzes space usage and clusters files for recommendations.
/// </summary>
public class SpaceAnalysisService
{
    private readonly ILogger<SpaceAnalysisService> _logger;
    private readonly DriveManagerService _driveManager;
    
    public SpaceAnalysisService(
        ILogger<SpaceAnalysisService> logger,
        DriveManagerService driveManager)
    {
        _logger = logger;
        _driveManager = driveManager;
    }
    
    /// <summary>
    /// Groups scanned files into clusters for recommendation.
    /// </summary>
    public async Task<List<FileCluster>> ClusterFilesAsync(
        IReadOnlyList<FileSystemEntry> files,
        List<InstalledApp> apps,
        CancellationToken ct = default)
    {
        var clusters = new List<FileCluster>();
        
        await Task.Run(() =>
        {
            // Group by parent directory
            var directoryGroups = files
                .GroupBy(f => Path.GetDirectoryName(f.Path) ?? "")
                .Where(g => g.Count() >= 5) // At least 5 files
                .ToList();
            
            foreach (var group in directoryGroups)
            {
                ct.ThrowIfCancellationRequested();
                
                var cluster = new FileCluster
                {
                    BasePath = group.Key,
                    Name = Path.GetFileName(group.Key) ?? group.Key,
                    FilePaths = group.Select(f => f.Path).ToList(),
                    TotalBytes = group.Sum(f => f.SizeBytes),
                    OldestFile = group.Min(f => f.Created),
                    NewestFile = group.Max(f => f.Created),
                    LastModified = group.Max(f => f.LastModified),
                    FileTypeDistribution = group
                        .GroupBy(f => f.Extension)
                        .ToDictionary(g => g.Key, g => g.Count())
                };
                
                // Categorize the cluster
                CategorizeCluster(cluster, apps);
                
                // Check if relocatable
                DetermineRelocationEligibility(cluster);
                
                clusters.Add(cluster);
            }
            
            // Also create clusters for specific categories
            AddSpecialClusters(clusters, files, apps, ct);
            
        }, ct);
        
        return clusters
            .OrderByDescending(c => c.TotalBytes)
            .ToList();
    }
    
    private void CategorizeCluster(FileCluster cluster, List<InstalledApp> apps)
    {
        var path = cluster.BasePath.ToLowerInvariant();
        var primaryExt = cluster.PrimaryFileType;
        
        // Check for app association
        foreach (var app in apps)
        {
            if (!string.IsNullOrEmpty(app.InstallPath) && 
                path.StartsWith(app.InstallPath.ToLowerInvariant()))
            {
                cluster.AssociatedApp = app.Name;
                cluster.AssociatedAppId = app.Id;
                break;
            }
            
            if (app.DataFolders.Any(df => path.StartsWith(df.ToLowerInvariant())))
            {
                cluster.AssociatedApp = app.Name;
                cluster.AssociatedAppId = app.Id;
                cluster.Type = FileClusterType.AppData;
                return;
            }
            
            if (app.CacheFolders.Any(cf => path.StartsWith(cf.ToLowerInvariant())))
            {
                cluster.AssociatedApp = app.Name;
                cluster.AssociatedAppId = app.Id;
                cluster.Type = FileClusterType.AppCache;
                return;
            }
        }
        
        // Categorize by path patterns
        if (path.Contains("\\temp") || path.Contains("\\tmp"))
        {
            cluster.Type = FileClusterType.TempFiles;
            cluster.Category = SpaceCategoryType.TempFiles;
        }
        else if (path.Contains("\\cache") || path.Contains("\\cached"))
        {
            cluster.Type = FileClusterType.AppCache;
            cluster.Category = SpaceCategoryType.BrowserCache;
        }
        else if (path.Contains("\\downloads"))
        {
            cluster.Type = FileClusterType.Downloads;
            cluster.Category = SpaceCategoryType.UserDownloads;
        }
        else if (path.Contains("\\documents"))
        {
            cluster.Type = FileClusterType.Documents;
            cluster.Category = SpaceCategoryType.UserDocuments;
        }
        else if (path.Contains("\\pictures") || path.Contains("\\photos"))
        {
            cluster.Type = FileClusterType.MediaPhotos;
            cluster.Category = SpaceCategoryType.UserMedia;
        }
        else if (path.Contains("\\videos") || path.Contains("\\movies"))
        {
            cluster.Type = FileClusterType.MediaVideos;
            cluster.Category = SpaceCategoryType.UserMedia;
        }
        else if (path.Contains("\\music"))
        {
            cluster.Type = FileClusterType.MediaMusic;
            cluster.Category = SpaceCategoryType.UserMedia;
        }
        else if (path.Contains("\\node_modules") || path.Contains("\\bin\\") || 
                 path.Contains("\\obj\\") || path.Contains("\\.nuget"))
        {
            cluster.Type = FileClusterType.DeveloperArtifacts;
            cluster.Category = SpaceCategoryType.Other;
        }
        else if (path.Contains("\\steam") || path.Contains("\\epic games") || 
                 path.Contains("\\origin") || path.Contains("\\gog"))
        {
            cluster.Type = FileClusterType.GameAssets;
            cluster.Category = SpaceCategoryType.Games;
        }
        // Categorize by file type
        else if (new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" }.Contains(primaryExt))
        {
            cluster.Type = FileClusterType.MediaPhotos;
            cluster.Category = SpaceCategoryType.UserMedia;
        }
        else if (new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv" }.Contains(primaryExt))
        {
            cluster.Type = FileClusterType.MediaVideos;
            cluster.Category = SpaceCategoryType.UserMedia;
        }
        else if (new[] { ".mp3", ".flac", ".wav", ".aac", ".ogg" }.Contains(primaryExt))
        {
            cluster.Type = FileClusterType.MediaMusic;
            cluster.Category = SpaceCategoryType.UserMedia;
        }
        else if (new[] { ".zip", ".rar", ".7z", ".tar", ".gz" }.Contains(primaryExt))
        {
            cluster.Type = FileClusterType.Archives;
            cluster.Category = SpaceCategoryType.Other;
        }
        else
        {
            cluster.Type = FileClusterType.Unknown;
            cluster.Category = SpaceCategoryType.Other;
        }
    }
    
    private void DetermineRelocationEligibility(FileCluster cluster)
    {
        // User data is relocatable
        cluster.CanRelocate = cluster.Type switch
        {
            FileClusterType.Documents => true,
            FileClusterType.Downloads => true,
            FileClusterType.MediaPhotos => true,
            FileClusterType.MediaVideos => true,
            FileClusterType.MediaMusic => true,
            FileClusterType.Archives => true,
            FileClusterType.GameAssets => true, // With junction
            FileClusterType.DeveloperArtifacts => false, // Usually break if moved
            FileClusterType.AppCache => false,
            FileClusterType.AppData => false,
            FileClusterType.TempFiles => false,
            _ => false
        };
        
        // Games typically require junctions
        cluster.RequiresJunction = cluster.Type == FileClusterType.GameAssets;
        
        // Get available drives for relocation
        if (cluster.CanRelocate)
        {
            var currentDrive = Path.GetPathRoot(cluster.BasePath) ?? "C:\\";
            cluster.AvailableDrives = _driveManager.GetRelocationTargets(
                cluster.TotalBytes, 
                currentDrive);
        }
    }
    
    private void AddSpecialClusters(
        List<FileCluster> clusters,
        IReadOnlyList<FileSystemEntry> files,
        List<InstalledApp> apps,
        CancellationToken ct)
    {
        // Old files cluster (not accessed in 1+ year)
        var oneYearAgo = DateTime.Now.AddYears(-1);
        var oldFiles = files
            .Where(f => f.LastAccessed < oneYearAgo)
            .Where(f => f.SizeBytes > 10 * 1024 * 1024) // >10MB
            .ToList();
        
        if (oldFiles.Any())
        {
            var oldCluster = new FileCluster
            {
                Name = "Old Unused Files",
                BasePath = "Various Locations",
                Type = FileClusterType.OldFiles,
                FilePaths = oldFiles.Select(f => f.Path).ToList(),
                TotalBytes = oldFiles.Sum(f => f.SizeBytes),
                OldestFile = oldFiles.Min(f => f.LastAccessed),
                NewestFile = oldFiles.Max(f => f.LastAccessed),
                LastModified = oldFiles.Max(f => f.LastModified),
                CanRelocate = true
            };
            clusters.Add(oldCluster);
        }
        
        // Large files cluster
        var largeFiles = files
            .Where(f => f.SizeBytes > 1024 * 1024 * 1024) // >1GB
            .OrderByDescending(f => f.SizeBytes)
            .Take(50)
            .ToList();
        
        if (largeFiles.Any())
        {
            var largeCluster = new FileCluster
            {
                Name = "Large Files (>1GB)",
                BasePath = "Various Locations",
                Type = FileClusterType.LargeFiles,
                FilePaths = largeFiles.Select(f => f.Path).ToList(),
                TotalBytes = largeFiles.Sum(f => f.SizeBytes),
                FileTypeDistribution = largeFiles
                    .GroupBy(f => f.Extension)
                    .ToDictionary(g => g.Key, g => g.Count()),
                CanRelocate = true
            };
            clusters.Add(largeCluster);
        }
    }
    
    /// <summary>
    /// Finds cleanup opportunities from the drive analysis.
    /// </summary>
    public async Task<List<CleanupOpportunity>> FindCleanupOpportunitiesAsync(
        List<DriveAnalysis> drives,
        List<InstalledApp> apps,
        CancellationToken ct = default)
    {
        var opportunities = new List<CleanupOpportunity>();
        
        await Task.Run(() =>
        {
            foreach (var drive in drives)
            {
                ct.ThrowIfCancellationRequested();
                var root = drive.DriveLetter;
                
                // Windows Temp
                AddTempOpportunity(opportunities, Path.Combine(root, "Windows", "Temp"), 
                    CleanupType.WindowsTemp, "Windows temporary files");
                
                // User Temp folders
                var usersPath = Path.Combine(root, "Users");
                if (Directory.Exists(usersPath))
                {
                    foreach (var userDir in Directory.GetDirectories(usersPath))
                    {
                        var userName = Path.GetFileName(userDir);
                        if (userName is "Public" or "Default" or "Default User") continue;
                        
                        AddTempOpportunity(opportunities, 
                            Path.Combine(userDir, "AppData", "Local", "Temp"),
                            CleanupType.UserTemp, $"User temp files ({userName})");
                        
                        // Browser caches
                        AddBrowserCacheOpportunities(opportunities, userDir, userName);
                        
                        // Thumbnail cache
                        AddTempOpportunity(opportunities,
                            Path.Combine(userDir, "AppData", "Local", "Microsoft", "Windows", "Explorer"),
                            CleanupType.ThumbnailCache, $"Thumbnail cache ({userName})");
                    }
                }
                
                // Windows Update cache
                AddTempOpportunity(opportunities, 
                    Path.Combine(root, "Windows", "SoftwareDistribution", "Download"),
                    CleanupType.WindowsUpdateCache, "Windows Update download cache");
                
                // Recycle Bin
                var recycleBin = Path.Combine(root, "$Recycle.Bin");
                if (Directory.Exists(recycleBin))
                {
                    var size = GetDirectorySizeSafe(recycleBin);
                    if (size > 0)
                    {
                        opportunities.Add(new CleanupOpportunity
                        {
                            Type = CleanupType.RecycleBin,
                            Path = recycleBin,
                            Description = "Recycle Bin contents",
                            Bytes = size,
                            Risk = CleanupRisk.Low,
                            Confidence = 0.95
                        });
                    }
                }
            }
            
            // App-specific caches
            foreach (var app in apps.Where(a => a.CacheSizeBytes > 10 * 1024 * 1024)) // >10MB cache
            {
                foreach (var cachePath in app.CacheFolders)
                {
                    if (Directory.Exists(cachePath))
                    {
                        opportunities.Add(new CleanupOpportunity
                        {
                            Type = CleanupType.AppCache,
                            Path = cachePath,
                            Description = $"{app.Name} cache",
                            AssociatedApp = app.Name,
                            Bytes = GetDirectorySizeSafe(cachePath),
                            Risk = CleanupRisk.Low,
                            Confidence = 0.85
                        });
                    }
                }
            }
            
        }, ct);
        
        return opportunities
            .Where(o => o.Bytes > 1024 * 1024) // >1MB
            .OrderByDescending(o => o.Bytes)
            .ToList();
    }
    
    private void AddTempOpportunity(
        List<CleanupOpportunity> opportunities,
        string path,
        CleanupType type,
        string description)
    {
        if (!Directory.Exists(path)) return;
        
        var size = GetDirectorySizeSafe(path);
        var fileCount = GetFileCountSafe(path);
        
        if (size > 0)
        {
            opportunities.Add(new CleanupOpportunity
            {
                Type = type,
                Path = path,
                Description = description,
                Bytes = size,
                FileCount = fileCount,
                Risk = CleanupRisk.None,
                Confidence = 0.95
            });
        }
    }
    
    private void AddBrowserCacheOpportunities(
        List<CleanupOpportunity> opportunities, 
        string userDir,
        string userName)
    {
        var browsers = new Dictionary<string, string[]>
        {
            ["Chrome"] = new[] { "AppData", "Local", "Google", "Chrome", "User Data", "Default", "Cache" },
            ["Edge"] = new[] { "AppData", "Local", "Microsoft", "Edge", "User Data", "Default", "Cache" },
            ["Firefox"] = new[] { "AppData", "Local", "Mozilla", "Firefox", "Profiles" },
            ["Brave"] = new[] { "AppData", "Local", "BraveSoftware", "Brave-Browser", "User Data", "Default", "Cache" }
        };
        
        foreach (var (browserName, pathParts) in browsers)
        {
            var cachePath = Path.Combine(new[] { userDir }.Concat(pathParts).ToArray());
            
            if (Directory.Exists(cachePath))
            {
                var size = GetDirectorySizeSafe(cachePath);
                if (size > 10 * 1024 * 1024) // >10MB
                {
                    opportunities.Add(new CleanupOpportunity
                    {
                        Type = CleanupType.BrowserCache,
                        Path = cachePath,
                        Description = $"{browserName} cache ({userName})",
                        AssociatedApp = browserName,
                        Bytes = size,
                        Risk = CleanupRisk.None,
                        Confidence = 0.95
                    });
                }
            }
        }
    }
    
    private long GetDirectorySizeSafe(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true
            }).Sum(f =>
            {
                try { return new FileInfo(f).Length; }
                catch { return 0; }
            });
        }
        catch
        {
            return 0;
        }
    }
    
    private int GetFileCountSafe(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true
            }).Count();
        }
        catch
        {
            return 0;
        }
    }
}
