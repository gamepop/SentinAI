using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using SentinAI.Shared.Models.DeepScan;
using DeepScanDuplicateFileEntry = SentinAI.Shared.Models.DeepScan.DuplicateFileEntry;
// Use type aliases to avoid conflict with DuplicateFileService
using DeepScanDuplicateGroup = SentinAI.Shared.Models.DeepScan.DuplicateGroup;

namespace SentinAI.Web.Services.DeepScan;

/// <summary>
/// High-performance file system scanner using native APIs.
/// </summary>
public class FileSystemScanner
{
    private readonly ILogger<FileSystemScanner> _logger;
    private readonly List<FileSystemEntry> _scannedFiles = new();
    private readonly object _lock = new();

    public FileSystemScanner(ILogger<FileSystemScanner> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets all files scanned in the current session.
    /// </summary>
    public IReadOnlyList<FileSystemEntry> GetScannedFiles()
    {
        lock (_lock)
        {
            return _scannedFiles.ToList();
        }
    }

    /// <summary>
    /// Scans a drive for files matching the options.
    /// </summary>
    public async Task ScanDriveAsync(
        string drivePath,
        DeepScanOptions options,
        Action<long, long, string> progressCallback,
        CancellationToken ct)
    {
        _logger.LogInformation("Starting file scan on {Drive}", drivePath);

        long filesScanned = 0;
        long bytesAnalyzed = 0;

        var excludePaths = options.ExcludePaths
            .Select(p => p.ToLowerInvariant())
            .ToHashSet();

        await Task.Run(() =>
        {
            try
            {
                ScanDirectory(
                    drivePath,
                    options,
                    excludePaths,
                    ref filesScanned,
                    ref bytesAnalyzed,
                    progressCallback,
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scanning {Path}", drivePath);
            }
        }, ct);

        _logger.LogInformation(
            "Completed scan of {Drive}: {Files} files, {Bytes} bytes",
            drivePath, filesScanned, bytesAnalyzed);
    }

    private void ScanDirectory(
        string path,
        DeepScanOptions options,
        HashSet<string> excludePaths,
        ref long filesScanned,
        ref long bytesAnalyzed,
        Action<long, long, string> progressCallback,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Check if path should be excluded
        if (excludePaths.Any(ex => path.ToLowerInvariant().Contains(ex)))
        {
            return;
        }

        try
        {
            // Use EnumerationOptions for better performance
            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = options.IncludeHiddenFiles
                    ? FileAttributes.System
                    : FileAttributes.Hidden | FileAttributes.System
            };

            // Scan files in current directory
            foreach (var file in Directory.EnumerateFiles(path, "*", enumOptions))
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var info = new FileInfo(file);

                    // Skip files smaller than minimum size
                    if (info.Length < options.MinFileSizeBytes)
                    {
                        continue;
                    }

                    var entry = new FileSystemEntry
                    {
                        Path = file,
                        Name = info.Name,
                        Extension = info.Extension.ToLowerInvariant(),
                        SizeBytes = info.Length,
                        LastModified = info.LastWriteTime,
                        LastAccessed = info.LastAccessTime,
                        Created = info.CreationTime,
                        IsDirectory = false
                    };

                    lock (_lock)
                    {
                        _scannedFiles.Add(entry);
                    }

                    filesScanned++;
                    bytesAnalyzed += info.Length;

                    // Update progress every 100 files
                    if (filesScanned % 100 == 0)
                    {
                        progressCallback(filesScanned, bytesAnalyzed, path);
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    // Skip inaccessible files
                }
            }

            // Recurse into subdirectories
            foreach (var dir in Directory.EnumerateDirectories(path, "*", enumOptions))
            {
                ScanDirectory(dir, options, excludePaths, ref filesScanned, ref bytesAnalyzed, progressCallback, ct);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            // Skip inaccessible directories
            _logger.LogDebug("Skipping inaccessible path: {Path}", path);
        }
    }

    /// <summary>
    /// Gets the size of a directory including all subdirectories.
    /// </summary>
    public async Task<DirectorySizeInfo> GetDirectorySizeAsync(string path, CancellationToken ct = default)
    {
        var result = new DirectorySizeInfo { Path = path };

        await Task.Run(() =>
        {
            try
            {
                CalculateDirectorySize(path, result, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating size for {Path}", path);
            }
        }, ct);

        return result;
    }

    private void CalculateDirectorySize(string path, DirectorySizeInfo result, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            foreach (var file in Directory.EnumerateFiles(path))
            {
                try
                {
                    var info = new FileInfo(file);
                    result.TotalBytes += info.Length;
                    result.FileCount++;
                }
                catch { }
            }

            foreach (var dir in Directory.EnumerateDirectories(path))
            {
                result.SubdirectoryCount++;
                CalculateDirectorySize(dir, result, ct);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Skip inaccessible
        }
    }

    /// <summary>
    /// Finds duplicate files by computing SHA256 hashes.
    /// </summary>
    public async Task<List<DeepScanDuplicateGroup>> FindDuplicatesAsync(
        IEnumerable<FileSystemEntry> files,
        CancellationToken ct = default)
    {
        var groups = new Dictionary<string, List<FileSystemEntry>>();

        // First group by size (quick filter)
        var sizeGroups = files
            .GroupBy(f => f.SizeBytes)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g);

        // Then compute hashes for potential duplicates
        await Parallel.ForEachAsync(
            sizeGroups,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (file, token) =>
            {
                try
                {
                    var hash = await ComputeFileHashAsync(file.Path, token);
                    lock (groups)
                    {
                        if (!groups.ContainsKey(hash))
                        {
                            groups[hash] = new List<FileSystemEntry>();
                        }
                        groups[hash].Add(file);
                    }
                }
                catch { }
            });

        // Build duplicate groups
        return groups
            .Where(g => g.Value.Count > 1)
            .Select(g => new DeepScanDuplicateGroup
            {
                Hash = g.Key,
                FileSize = g.Value.First().SizeBytes,
                Files = g.Value.Select(f => new DeepScanDuplicateFileEntry
                {
                    Path = f.Path,
                    Name = f.Name,
                    LastAccessed = f.LastAccessed,
                    LastModified = f.LastModified,
                    DriveLetter = System.IO.Path.GetPathRoot(f.Path) ?? "",
                    LocationPriority = GetLocationPriority(f.Path)
                }).ToList()
            })
            .OrderByDescending(g => g.WastedBytes)
            .ToList();
    }

    private async Task<string> ComputeFileHashAsync(string path, CancellationToken ct)
    {
        using var sha256 = SHA256.Create();
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        var hash = await sha256.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash);
    }

    private int GetLocationPriority(string path)
    {
        // Lower = more important location (should keep)
        var lower = path.ToLowerInvariant();

        if (lower.Contains("documents")) return 1;
        if (lower.Contains("pictures")) return 1;
        if (lower.Contains("desktop")) return 2;
        if (lower.Contains("downloads")) return 3;
        if (lower.Contains("appdata")) return 4;
        if (lower.Contains("temp")) return 5;

        return 3;
    }

    /// <summary>
    /// Clears the scanned files cache.
    /// </summary>
    public void ClearCache()
    {
        lock (_lock)
        {
            _scannedFiles.Clear();
        }
    }
}

/// <summary>
/// Represents a file or directory entry from scanning.
/// </summary>
public class FileSystemEntry
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Extension { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime LastModified { get; set; }
    public DateTime LastAccessed { get; set; }
    public DateTime Created { get; set; }
    public bool IsDirectory { get; set; }

    public string SizeFormatted => FormatBytes(SizeBytes);

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }
}

/// <summary>
/// Directory size calculation result.
/// </summary>
public class DirectorySizeInfo
{
    public string Path { get; set; } = "";
    public long TotalBytes { get; set; }
    public int FileCount { get; set; }
    public int SubdirectoryCount { get; set; }

    public string TotalBytesFormatted => FormatBytes(TotalBytes);

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }
}
