using Microsoft.Extensions.Logging;

namespace SentinAI.SentinelService.Services;

public interface ICleanupExecutor
{
    Task<CleanupExecutionResult> ExecuteCleanupAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken);
}

public class CleanupExecutionResult
{
    public bool Success { get; set; }
    public int FilesDeleted { get; set; }
    public long BytesFreed { get; set; }
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Safely executes cleanup operations with proper error handling
/// </summary>
public class CleanupExecutor : ICleanupExecutor
{
    private readonly ILogger<CleanupExecutor> _logger;

    public CleanupExecutor(ILogger<CleanupExecutor> logger)
    {
        _logger = logger;
    }

    public async Task<CleanupExecutionResult> ExecuteCleanupAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken)
    {
        var result = new CleanupExecutionResult { Success = true };

        foreach (var filePath in filePaths)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                result.Success = false;
                result.Errors.Add("Operation cancelled by user");
                break;
            }

            try
            {
                await DeleteFileOrDirectoryAsync(filePath, result, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting {Path}", filePath);
                result.Errors.Add($"{filePath}: {ex.Message}");
                result.Success = false;
            }
        }

        _logger.LogInformation(
            "Cleanup completed: {FilesDeleted} files deleted, {BytesFreed} bytes freed, {Errors} errors",
            result.FilesDeleted,
            result.BytesFreed,
            result.Errors.Count);

        return result;
    }

    private async Task DeleteFileOrDirectoryAsync(
        string path,
        CleanupExecutionResult result,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            var fileInfo = new FileInfo(path);
            long size = fileInfo.Length;

            File.Delete(path);

            result.FilesDeleted++;
            result.BytesFreed += size;

            _logger.LogInformation("Deleted file: {Path} ({Size} bytes)", path, size);
        }
        else if (Directory.Exists(path))
        {
            await DeleteDirectoryRecursiveAsync(path, result, cancellationToken);
        }
        else
        {
            _logger.LogWarning("Path not found: {Path}", path);
        }
    }

    private async Task DeleteDirectoryRecursiveAsync(
        string dirPath,
        CleanupExecutionResult result,
        CancellationToken cancellationToken)
    {
        var dirInfo = new DirectoryInfo(dirPath);

        // Delete all files
        foreach (var file in dirInfo.GetFiles())
        {
            if (cancellationToken.IsCancellationRequested) return;

            try
            {
                long size = file.Length;
                file.Delete();
                result.FilesDeleted++;
                result.BytesFreed += size;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete file: {Path}", file.FullName);
                result.Errors.Add($"{file.FullName}: {ex.Message}");
            }
        }

        // Recursively delete subdirectories
        foreach (var subDir in dirInfo.GetDirectories())
        {
            if (cancellationToken.IsCancellationRequested) return;
            await DeleteDirectoryRecursiveAsync(subDir.FullName, result, cancellationToken);
        }

        // Delete the directory itself
        try
        {
            dirInfo.Delete();
            _logger.LogInformation("Deleted directory: {Path}", dirPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete directory: {Path}", dirPath);
            result.Errors.Add($"{dirPath}: {ex.Message}");
        }

        await Task.CompletedTask;
    }
}
