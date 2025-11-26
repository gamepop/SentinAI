using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using SentinAI.Shared.Models;

namespace SentinAI.Web.Services;

public interface IDuplicateFileService
{
    Task<DuplicateScanResult> ScanForDuplicatesAsync(
        string rootPath,
        DuplicateScanOptions options,
        IProgress<DuplicateScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<List<DuplicateGroup>> GetDuplicateGroupsAsync();
    Task<string?> GetLastScanRootPathAsync();
    Task<DeleteDuplicatesResult> DeleteFilesAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default);
    void ClearCache();
}

public class DuplicateScanOptions
{
    public long MinFileSizeBytes { get; set; } = 1024; // 1KB minimum
    public long MaxFileSizeBytes { get; set; } = 10L * 1024 * 1024 * 1024; // 10GB max
    public string[] ExcludedExtensions { get; set; } = { ".sys", ".dll", ".exe", ".msi" };
    public string[] ExcludedFolders { get; set; } = { "Windows", "Program Files", "Program Files (x86)", "$Recycle.Bin" };
    public bool IncludeHiddenFiles { get; set; } = false;
    public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;
    public bool IncludeSubdirectories { get; set; } = true;
}

public class DuplicateScanProgress
{
    public int FilesScanned { get; set; }
    public int FilesHashed { get; set; }
    public int DuplicateGroupsFound { get; set; }
    public long BytesProcessed { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public string Phase { get; set; } = "Scanning"; // Scanning, Grouping, Hashing, Complete
    public double ProgressPercent { get; set; }
}

public class DuplicateScanResult
{
    public List<DuplicateGroup> DuplicateGroups { get; set; } = new();
    public int TotalFilesScanned { get; set; }
    public int TotalDuplicateFiles { get; set; }
    public long TotalWastedBytes { get; set; }
    public TimeSpan ScanDuration { get; set; }
    public DateTime ScanTime { get; set; } = DateTime.UtcNow;
    public string RootPath { get; set; } = string.Empty;
}

public class DuplicateGroup
{
    public string Hash { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public List<DuplicateFile> Files { get; set; } = new();
    public long WastedBytes => FileSize * (Files.Count - 1);
    public int DuplicateCount => Files.Count - 1;
}

public class DuplicateFile
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime LastModified { get; set; }
    public DateTime Created { get; set; }
    public bool IsOriginal { get; set; } // Oldest file in group
    public bool MarkedForDeletion { get; set; }
}

public class DeleteDuplicatesResult
{
    public int DeletedCount { get; set; }
    public long BytesFreed { get; set; }
    public List<string> Errors { get; } = new();
}

/// <summary>
/// Service to detect duplicate files using size grouping and hash comparison.
/// Optimization: Only hash files with matching sizes.
/// </summary>
public class DuplicateFileService : IDuplicateFileService
{
    private readonly ILogger<DuplicateFileService> _logger;
    private readonly ConcurrentDictionary<string, DuplicateGroup> _duplicateCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();
    private DuplicateScanResult? _lastScanResult;
    private string? _lastScanRootPath;

    public DuplicateFileService(ILogger<DuplicateFileService> logger)
    {
        _logger = logger;
    }

    public async Task<DuplicateScanResult> ScanForDuplicatesAsync(
        string rootPath,
        DuplicateScanOptions options,
        IProgress<DuplicateScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("🔍 Starting duplicate file scan at {Path}", rootPath);

        var result = new DuplicateScanResult();
        var progressReport = new DuplicateScanProgress();
        result.RootPath = rootPath;
        _lastScanRootPath = rootPath;

        try
        {
            // Phase 1: Collect all files and group by size
            progressReport.Phase = "Scanning";
            progress?.Report(progressReport);

            var filesBySize = await CollectFilesGroupedBySizeAsync(
                rootPath, options, progressReport, progress, cancellationToken);

            result.TotalFilesScanned = progressReport.FilesScanned;
            _logger.LogInformation("📁 Found {Count} files, {Groups} size groups with potential duplicates",
                progressReport.FilesScanned, filesBySize.Count(g => g.Value.Count > 1));

            // Phase 2: Hash files in same-size groups
            progressReport.Phase = "Hashing";
            progress?.Report(progressReport);

            var duplicateGroups = await HashAndGroupDuplicatesAsync(
                filesBySize, options, progressReport, progress, cancellationToken);

            // Phase 3: Build results
            progressReport.Phase = "Complete";
            progressReport.ProgressPercent = 100;

            lock (_cacheLock)
            {
                _duplicateCache.Clear();

                foreach (var group in duplicateGroups)
                {
                    _duplicateCache[group.Hash] = group;
                    result.DuplicateGroups.Add(group);
                    result.TotalDuplicateFiles += group.DuplicateCount;
                    result.TotalWastedBytes += group.WastedBytes;
                }
            }

            progressReport.DuplicateGroupsFound = result.DuplicateGroups.Count;
            progress?.Report(progressReport);

            sw.Stop();
            result.ScanDuration = sw.Elapsed;
            _lastScanResult = result;

            _logger.LogInformation(
                "✅ Duplicate scan complete: {Groups} groups, {Files} duplicates, {Wasted} wasted",
                result.DuplicateGroups.Count,
                result.TotalDuplicateFiles,
                FormatBytes(result.TotalWastedBytes));

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("⚠️ Duplicate scan cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Duplicate scan failed");
            throw;
        }
    }

    public Task<List<DuplicateGroup>> GetDuplicateGroupsAsync()
    {
        lock (_cacheLock)
        {
            var snapshot = _duplicateCache.Values
                .Select(CloneGroup)
                .ToList();

            return Task.FromResult(snapshot);
        }
    }

    public Task<string?> GetLastScanRootPathAsync()
    {
        return Task.FromResult(_lastScanRootPath);
    }

    public Task<DeleteDuplicatesResult> DeleteFilesAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        var result = new DeleteDuplicatesResult();
        var uniquePaths = new HashSet<string>(
            filePaths.Where(p => !string.IsNullOrWhiteSpace(p)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var path in uniquePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!File.Exists(path))
                {
                    result.Errors.Add($"{path}: File not found");
                    continue;
                }

                long fileSize = 0;
                try
                {
                    var info = new FileInfo(path);
                    fileSize = info.Length;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not read file size for {File}", path);
                }

                File.Delete(path);

                result.DeletedCount++;
                result.BytesFreed += fileSize;

                RemoveFileFromCache(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete duplicate file {File}", path);
                result.Errors.Add($"{path}: {ex.Message}");
            }
        }

        return Task.FromResult(result);
    }

    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _duplicateCache.Clear();
            _lastScanResult = null;
            _lastScanRootPath = null;
        }
    }

    private static DuplicateGroup CloneGroup(DuplicateGroup group)
    {
        return new DuplicateGroup
        {
            Hash = group.Hash,
            FileSize = group.FileSize,
            Files = group.Files
                .Select(f => new DuplicateFile
                {
                    FilePath = f.FilePath,
                    FileName = f.FileName,
                    FileSize = f.FileSize,
                    LastModified = f.LastModified,
                    Created = f.Created,
                    IsOriginal = f.IsOriginal,
                    MarkedForDeletion = f.MarkedForDeletion
                })
                .ToList()
        };
    }

    private void RemoveFileFromCache(string filePath)
    {
        lock (_cacheLock)
        {
            foreach (var entry in _duplicateCache.ToArray())
            {
                var group = entry.Value;
                var removed = group.Files.RemoveAll(f =>
                    string.Equals(f.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

                if (removed > 0)
                {
                    if (group.Files.Count < 2)
                    {
                        _duplicateCache.TryRemove(entry.Key, out _);
                    }
                }
            }
        }
    }

    private async Task<Dictionary<long, List<FileInfo>>> CollectFilesGroupedBySizeAsync(
        string rootPath,
        DuplicateScanOptions options,
        DuplicateScanProgress progressReport,
        IProgress<DuplicateScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var filesBySize = new Dictionary<long, List<FileInfo>>();
        var excludedFolders = new HashSet<string>(options.ExcludedFolders, StringComparer.OrdinalIgnoreCase);
        var excludedExtensions = new HashSet<string>(options.ExcludedExtensions, StringComparer.OrdinalIgnoreCase);

        await Task.Run(() =>
        {
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(rootPath));

            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var dir = stack.Pop();

                // Skip excluded folders
                if (excludedFolders.Contains(dir.Name))
                    continue;

                try
                {
                    // Process files in current directory
                    foreach (var file in dir.EnumerateFiles())
                    {
                        try
                        {
                            // Skip hidden files if configured
                            if (!options.IncludeHiddenFiles && (file.Attributes & FileAttributes.Hidden) != 0)
                                continue;

                            // Skip system files
                            if ((file.Attributes & FileAttributes.System) != 0)
                                continue;

                            // Skip excluded extensions
                            if (excludedExtensions.Contains(file.Extension))
                                continue;

                            // Skip files outside size range
                            if (file.Length < options.MinFileSizeBytes || file.Length > options.MaxFileSizeBytes)
                                continue;

                            // Group by size
                            if (!filesBySize.TryGetValue(file.Length, out var sizeGroup))
                            {
                                sizeGroup = new List<FileInfo>();
                                filesBySize[file.Length] = sizeGroup;
                            }
                            sizeGroup.Add(file);

                            progressReport.FilesScanned++;
                            progressReport.CurrentFile = file.FullName;

                            if (progressReport.FilesScanned % 1000 == 0)
                            {
                                progress?.Report(progressReport);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Could not access file {File}", file.FullName);
                        }
                    }

                    if (options.IncludeSubdirectories)
                    {
                        foreach (var subDir in dir.EnumerateDirectories())
                        {
                            try
                            {
                                if (!excludedFolders.Contains(subDir.Name) &&
                                    (subDir.Attributes & FileAttributes.ReparsePoint) == 0) // Skip symlinks
                                {
                                    stack.Push(subDir);
                                }
                            }
                            catch { /* Access denied */ }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not access directory {Dir}", dir.FullName);
                }
            }
        }, cancellationToken);

        // Remove size groups with only one file (no duplicates possible)
        return filesBySize
            .Where(kvp => kvp.Value.Count > 1)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    private async Task<List<DuplicateGroup>> HashAndGroupDuplicatesAsync(
        Dictionary<long, List<FileInfo>> filesBySize,
        DuplicateScanOptions options,
        DuplicateScanProgress progressReport,
        IProgress<DuplicateScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var duplicateGroups = new ConcurrentBag<DuplicateGroup>();
        var totalFilesToHash = filesBySize.Values.Sum(g => g.Count);
        var filesHashed = 0;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(filesBySize, parallelOptions, async (sizeGroup, ct) =>
        {
            var hashGroups = new Dictionary<string, List<FileInfo>>();

            foreach (var file in sizeGroup.Value)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var hash = await ComputeFileHashAsync(file.FullName, ct);

                    if (!hashGroups.TryGetValue(hash, out var hashGroup))
                    {
                        hashGroup = new List<FileInfo>();
                        hashGroups[hash] = hashGroup;
                    }
                    hashGroup.Add(file);

                    Interlocked.Increment(ref filesHashed);
                    progressReport.FilesHashed = filesHashed;
                    progressReport.BytesProcessed += file.Length;
                    progressReport.ProgressPercent = (double)filesHashed / totalFilesToHash * 100;
                    progressReport.CurrentFile = file.FullName;

                    if (filesHashed % 100 == 0)
                    {
                        progress?.Report(progressReport);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not hash file {File}", file.FullName);
                }
            }

            // Create duplicate groups for hashes with multiple files
            foreach (var (hash, files) in hashGroups.Where(g => g.Value.Count > 1))
            {
                var orderedFiles = files.OrderBy(f => f.CreationTimeUtc).ToList();
                var group = new DuplicateGroup
                {
                    Hash = hash,
                    FileSize = sizeGroup.Key,
                    Files = orderedFiles.Select((f, i) => new DuplicateFile
                    {
                        FilePath = f.FullName,
                        FileName = f.Name,
                        FileSize = f.Length,
                        LastModified = f.LastWriteTimeUtc,
                        Created = f.CreationTimeUtc,
                        IsOriginal = i == 0 // First (oldest) is original
                    }).ToList()
                };

                duplicateGroups.Add(group);
            }
        });

        return duplicateGroups
            .OrderByDescending(g => g.WastedBytes)
            .ToList();
    }

    private static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken cancellationToken)
    {
        using var sha256 = SHA256.Create();
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024, // 1MB buffer
            useAsync: true);

        var hashBytes = await sha256.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hashBytes);
    }

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
