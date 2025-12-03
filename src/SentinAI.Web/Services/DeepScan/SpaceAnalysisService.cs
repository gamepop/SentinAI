using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using SentinAI.Shared.Models.DeepScan;

namespace SentinAI.Web.Services.DeepScan;

/// <summary>
/// Service for analyzing disk space usage and finding cleanup opportunities.
/// </summary>
[SupportedOSPlatform("windows")]
public class SpaceAnalysisService
{
    private readonly ILogger<SpaceAnalysisService> _logger;

    public SpaceAnalysisService(ILogger<SpaceAnalysisService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Finds cleanup opportunities on a drive.
    /// </summary>
    public async Task<List<CleanupOpportunity>> FindCleanupOpportunitiesAsync(
        string drivePath,
        CancellationToken ct = default)
    {
        var opportunities = new List<CleanupOpportunity>();

        await Task.Run(() =>
        {
            // Windows Temp
            var windowsTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
            AddTempFolderOpportunity(opportunities, windowsTemp, CleanupType.WindowsTemp, "Windows temporary files");

            // User Temp
            var userTemp = Path.GetTempPath();
            AddTempFolderOpportunity(opportunities, userTemp, CleanupType.UserTemp, "User temporary files");

            // Browser caches
            AddBrowserCacheOpportunities(opportunities);

            // Thumbnail cache
            var thumbCache = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "Explorer");
            AddThumbnailCacheOpportunity(opportunities, thumbCache);

            // Windows Update cache
            var updateCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "Download");
            AddTempFolderOpportunity(opportunities, updateCache, CleanupType.WindowsUpdateCache, "Windows Update download cache");

            // Recycle Bin
            AddRecycleBinOpportunity(opportunities, drivePath);

        }, ct);

        _logger.LogInformation("Found {Count} cleanup opportunities", opportunities.Count);
        return opportunities.Where(o => o.Bytes > 0).OrderByDescending(o => o.Bytes).ToList();
    }

    /// <summary>
    /// Identifies file clusters that could be relocated.
    /// </summary>
    public async Task<List<FileCluster>> IdentifyRelocationCandidatesAsync(
        IEnumerable<FileSystemEntry> files,
        List<AvailableDrive> availableDrives,
        CancellationToken ct = default)
    {
        var clusters = new List<FileCluster>();

        await Task.Run(() =>
        {
            // Group by common directories and file types
            var videoFiles = files.Where(f => IsVideoFile(f.Extension)).ToList();
            var photoFiles = files.Where(f => IsPhotoFile(f.Extension)).ToList();
            var archiveFiles = files.Where(f => IsArchiveFile(f.Extension)).ToList();

            // Create clusters for significant groupings
            if (videoFiles.Any())
            {
                var videoCluster = CreateCluster(videoFiles, FileClusterType.MediaVideos, "Video Files", availableDrives);
                if (videoCluster.TotalBytes > 500 * 1024 * 1024) // >500MB
                    clusters.Add(videoCluster);
            }

            if (photoFiles.Any())
            {
                var photoCluster = CreateCluster(photoFiles, FileClusterType.MediaPhotos, "Photo Collection", availableDrives);
                if (photoCluster.TotalBytes > 100 * 1024 * 1024) // >100MB
                    clusters.Add(photoCluster);
            }

            if (archiveFiles.Any())
            {
                var archiveCluster = CreateCluster(archiveFiles, FileClusterType.Archives, "Archive Files", availableDrives);
                if (archiveCluster.TotalBytes > 100 * 1024 * 1024) // >100MB
                    clusters.Add(archiveCluster);
            }

            // Find Downloads folder
            var downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads";
            var downloadFiles = files.Where(f => f.Path.StartsWith(downloads, StringComparison.OrdinalIgnoreCase)).ToList();
            if (downloadFiles.Any())
            {
                var downloadCluster = new FileCluster
                {
                    Name = "Downloads Folder",
                    BasePath = downloads,
                    Type = FileClusterType.Downloads,
                    PrimaryFileType = "mixed",
                    TotalBytes = downloadFiles.Sum(f => f.SizeBytes),
                    FileCount = downloadFiles.Count,
                    CanRelocate = true,
                    AvailableDrives = availableDrives
                };
                if (downloadCluster.TotalBytes > 500 * 1024 * 1024) // >500MB
                    clusters.Add(downloadCluster);
            }

        }, ct);

        return clusters.OrderByDescending(c => c.TotalBytes).ToList();
    }

    private void AddTempFolderOpportunity(List<CleanupOpportunity> opportunities, string path, CleanupType type, string description)
    {
        if (!Directory.Exists(path)) return;

        try
        {
            var (bytes, count) = GetFolderStats(path);
            if (bytes > 0)
            {
                opportunities.Add(new CleanupOpportunity
                {
                    Type = type,
                    Path = path,
                    Description = description,
                    Bytes = bytes,
                    FileCount = count,
                    Risk = CleanupRisk.None
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error analyzing {Path}", path);
        }
    }

    private void AddBrowserCacheOpportunities(List<CleanupOpportunity> opportunities)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // Chrome
        var chromeCache = Path.Combine(localAppData, "Google", "Chrome", "User Data", "Default", "Cache");
        if (Directory.Exists(chromeCache))
        {
            var (bytes, count) = GetFolderStats(chromeCache);
            opportunities.Add(new CleanupOpportunity
            {
                Type = CleanupType.BrowserCache,
                Path = chromeCache,
                Description = "Google Chrome cache",
                Bytes = bytes,
                FileCount = count,
                Risk = CleanupRisk.None,
                AssociatedApp = "Google Chrome"
            });
        }

        // Edge
        var edgeCache = Path.Combine(localAppData, "Microsoft", "Edge", "User Data", "Default", "Cache");
        if (Directory.Exists(edgeCache))
        {
            var (bytes, count) = GetFolderStats(edgeCache);
            opportunities.Add(new CleanupOpportunity
            {
                Type = CleanupType.BrowserCache,
                Path = edgeCache,
                Description = "Microsoft Edge cache",
                Bytes = bytes,
                FileCount = count,
                Risk = CleanupRisk.None,
                AssociatedApp = "Microsoft Edge"
            });
        }

        // Firefox
        var firefoxPath = Path.Combine(localAppData, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(firefoxPath))
        {
            try
            {
                foreach (var profile in Directory.GetDirectories(firefoxPath))
                {
                    var cache = Path.Combine(profile, "cache2");
                    if (Directory.Exists(cache))
                    {
                        var (bytes, count) = GetFolderStats(cache);
                        opportunities.Add(new CleanupOpportunity
                        {
                            Type = CleanupType.BrowserCache,
                            Path = cache,
                            Description = "Mozilla Firefox cache",
                            Bytes = bytes,
                            FileCount = count,
                            Risk = CleanupRisk.None,
                            AssociatedApp = "Mozilla Firefox"
                        });
                        break;
                    }
                }
            }
            catch { }
        }
    }

    private void AddThumbnailCacheOpportunity(List<CleanupOpportunity> opportunities, string path)
    {
        if (!Directory.Exists(path)) return;

        try
        {
            var thumbFiles = Directory.GetFiles(path, "thumbcache*.db", SearchOption.TopDirectoryOnly);
            var bytes = thumbFiles.Sum(f => new FileInfo(f).Length);
            if (bytes > 0)
            {
                opportunities.Add(new CleanupOpportunity
                {
                    Type = CleanupType.ThumbnailCache,
                    Path = path,
                    Description = "Windows thumbnail cache (will rebuild automatically)",
                    Bytes = bytes,
                    FileCount = thumbFiles.Length,
                    Risk = CleanupRisk.Low
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error analyzing thumbnail cache at {Path}", path);
        }
    }

    private void AddRecycleBinOpportunity(List<CleanupOpportunity> opportunities, string drivePath)
    {
        var recycleBinPath = Path.Combine(drivePath, "$Recycle.Bin");
        if (!Directory.Exists(recycleBinPath)) return;

        try
        {
            long totalBytes = 0;
            int fileCount = 0;

            foreach (var userBin in Directory.GetDirectories(recycleBinPath))
            {
                try
                {
                    var (bytes, count) = GetFolderStats(userBin);
                    totalBytes += bytes;
                    fileCount += count;
                }
                catch { }
            }

            if (totalBytes > 0)
            {
                opportunities.Add(new CleanupOpportunity
                {
                    Type = CleanupType.RecycleBin,
                    Path = recycleBinPath,
                    Description = "Recycle Bin contents",
                    Bytes = totalBytes,
                    FileCount = fileCount,
                    Risk = CleanupRisk.Medium // Medium because user may want to restore
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error analyzing Recycle Bin at {Path}", recycleBinPath);
        }
    }

    private (long bytes, int count) GetFolderStats(string path)
    {
        long bytes = 0;
        int count = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true
            }))
            {
                try
                {
                    var info = new FileInfo(file);
                    bytes += info.Length;
                    count++;
                }
                catch { }
            }
        }
        catch { }

        return (bytes, count);
    }

    private FileCluster CreateCluster(
        List<FileSystemEntry> files,
        FileClusterType type,
        string name,
        List<AvailableDrive> availableDrives)
    {
        var basePath = FindCommonPath(files.Select(f => f.Path).ToList());
        var primaryExtension = files
            .GroupBy(f => f.Extension)
            .OrderByDescending(g => g.Sum(f => f.SizeBytes))
            .First().Key;

        return new FileCluster
        {
            Name = name,
            BasePath = basePath,
            Type = type,
            PrimaryFileType = primaryExtension,
            TotalBytes = files.Sum(f => f.SizeBytes),
            FileCount = files.Count,
            CanRelocate = true,
            AvailableDrives = availableDrives
        };
    }

    private string FindCommonPath(List<string> paths)
    {
        if (!paths.Any()) return "";
        if (paths.Count == 1) return Path.GetDirectoryName(paths[0]) ?? "";

        var first = paths[0];
        var common = first;

        foreach (var path in paths.Skip(1))
        {
            while (!path.StartsWith(common, StringComparison.OrdinalIgnoreCase) && common.Length > 3)
            {
                common = Path.GetDirectoryName(common) ?? "";
            }
        }

        return common;
    }

    private static bool IsVideoFile(string extension) =>
        new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v" }
            .Contains(extension.ToLowerInvariant());

    private static bool IsPhotoFile(string extension) =>
        new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".raw", ".heic", ".webp" }
            .Contains(extension.ToLowerInvariant());

    private static bool IsArchiveFile(string extension) =>
        new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".iso" }
            .Contains(extension.ToLowerInvariant());
}
