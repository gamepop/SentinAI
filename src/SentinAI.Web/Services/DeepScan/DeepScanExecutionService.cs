using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SentinAI.Shared.Models.DeepScan;

namespace SentinAI.Web.Services.DeepScan;

/// <summary>
/// Results from an execution operation.
/// </summary>
public class ExecutionResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public long BytesFreed { get; set; }
    public int ItemsProcessed { get; set; }
    public int ItemsFailed { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> SuccessfulItems { get; set; } = new();
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Progress information for execution operations.
/// </summary>
public class ExecutionProgress
{
    public string Phase { get; set; } = "Initializing";
    public int TotalItems { get; set; }
    public int CompletedItems { get; set; }
    public int FailedItems { get; set; }
    public string CurrentItem { get; set; } = string.Empty;
    public double ProgressPercent => TotalItems > 0 ? (double)CompletedItems / TotalItems * 100 : 0;
    public long BytesFreed { get; set; }
}

/// <summary>
/// Service for executing approved Deep Scan recommendations.
/// Handles cleanup, app removal, file relocation, and duplicate removal.
/// </summary>
[SupportedOSPlatform("windows")]
public class DeepScanExecutionService
{
    private readonly ILogger<DeepScanExecutionService> _logger;
    private readonly IDuplicateFileService _duplicateService;
    private readonly IDeepScanSessionStore _sessionStore;
    private readonly DeepScanLearningService _learningService;

    public DeepScanExecutionService(
        ILogger<DeepScanExecutionService> logger,
        IDuplicateFileService duplicateService,
        IDeepScanSessionStore sessionStore,
        DeepScanLearningService learningService)
    {
        _logger = logger;
        _duplicateService = duplicateService;
        _sessionStore = sessionStore;
        _learningService = learningService;
    }

    #region Cleanup Execution

    /// <summary>
    /// Executes approved cleanup opportunities (temp files, caches, etc.).
    /// </summary>
    public async Task<ExecutionResult> ExecuteCleanupAsync(
        DeepScanSession session,
        IProgress<ExecutionProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new ExecutionResult();
        var execProgress = new ExecutionProgress { Phase = "Cleaning up files" };

        var approvedCleanups = session.CleanupOpportunities?
            .Where(o => o.Status == RecommendationStatus.Approved)
            .ToList() ?? new();

        execProgress.TotalItems = approvedCleanups.Count;
        progress?.Report(execProgress);

        _logger.LogInformation("Starting cleanup execution for {Count} approved items", approvedCleanups.Count);

        foreach (var cleanup in approvedCleanups)
        {
            ct.ThrowIfCancellationRequested();

            execProgress.CurrentItem = cleanup.Path;
            progress?.Report(execProgress);

            try
            {
                var (success, bytesFreed, error) = await ExecuteSingleCleanupAsync(cleanup, ct);

                if (success)
                {
                    result.BytesFreed += bytesFreed;
                    result.ItemsProcessed++;
                    result.SuccessfulItems.Add(cleanup.Path);
                    cleanup.Status = RecommendationStatus.Executed;
                    execProgress.BytesFreed += bytesFreed;
                }
                else
                {
                    result.ItemsFailed++;
                    result.Errors.Add($"{cleanup.Path}: {error}");
                    cleanup.Status = RecommendationStatus.Failed;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clean up {Path}", cleanup.Path);
                result.ItemsFailed++;
                result.Errors.Add($"{cleanup.Path}: {ex.Message}");
                cleanup.Status = RecommendationStatus.Failed;
            }

            execProgress.CompletedItems++;
            progress?.Report(execProgress);
        }

        sw.Stop();
        result.Duration = sw.Elapsed;
        result.Success = result.ItemsFailed == 0;
        result.Message = $"Cleaned {result.ItemsProcessed} items, freed {FormatBytes(result.BytesFreed)}";

        // Save updated session
        await _sessionStore.SaveSessionAsync(session);

        _logger.LogInformation(
            "Cleanup complete: {Processed} succeeded, {Failed} failed, {Bytes} freed",
            result.ItemsProcessed, result.ItemsFailed, FormatBytes(result.BytesFreed));

        return result;
    }

    private async Task<(bool Success, long BytesFreed, string? Error)> ExecuteSingleCleanupAsync(
        CleanupOpportunity cleanup,
        CancellationToken ct)
    {
        long bytesFreed = 0;

        try
        {
            if (Directory.Exists(cleanup.Path))
            {
                // It's a directory - delete contents or entire directory based on type
                var dirInfo = new DirectoryInfo(cleanup.Path);

                // For temp, cache, and log folders, delete contents but keep folder structure
                if (cleanup.Type == CleanupType.WindowsTemp ||
                    cleanup.Type == CleanupType.UserTemp ||
                    cleanup.Type == CleanupType.AppCache ||
                    cleanup.Type == CleanupType.BrowserCache ||
                    cleanup.Type == CleanupType.ThumbnailCache ||
                    cleanup.Type == CleanupType.LogFiles)
                {
                    // Delete contents but keep the folder
                    foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            bytesFreed += file.Length;
                            file.Delete();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Could not delete file {File}", file.FullName);
                            bytesFreed -= file.Length; // Didn't actually free this
                        }
                    }

                    // Try to delete empty subdirectories
                    foreach (var subDir in dirInfo.EnumerateDirectories("*", SearchOption.AllDirectories)
                        .OrderByDescending(d => d.FullName.Length)) // Delete deepest first
                    {
                        try
                        {
                            if (!subDir.EnumerateFileSystemInfos().Any())
                            {
                                subDir.Delete();
                            }
                        }
                        catch { /* Ignore - might not be empty */ }
                    }
                }
                else
                {
                    // Delete entire directory
                    bytesFreed = GetDirectorySize(dirInfo);
                    await Task.Run(() => dirInfo.Delete(true), ct);
                }
            }
            else if (File.Exists(cleanup.Path))
            {
                // It's a single file
                var fileInfo = new FileInfo(cleanup.Path);
                bytesFreed = fileInfo.Length;
                fileInfo.Delete();
            }
            else
            {
                return (false, 0, "Path not found");
            }

            return (true, bytesFreed, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (false, 0, "Access denied");
        }
        catch (IOException ex)
        {
            return (false, 0, ex.Message);
        }
    }

    #endregion

    #region App Uninstallation

    /// <summary>
    /// Executes approved app removal recommendations.
    /// </summary>
    public async Task<ExecutionResult> ExecuteAppRemovalAsync(
        DeepScanSession session,
        IProgress<ExecutionProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new ExecutionResult();
        var execProgress = new ExecutionProgress { Phase = "Removing applications" };

        var approvedApps = session.AppRemovalRecommendations?
            .Where(r => r.Status == RecommendationStatus.Approved)
            .ToList() ?? new();

        execProgress.TotalItems = approvedApps.Count;
        progress?.Report(execProgress);

        _logger.LogInformation("Starting app removal for {Count} approved apps", approvedApps.Count);

        foreach (var appRec in approvedApps)
        {
            ct.ThrowIfCancellationRequested();

            var app = appRec.App;
            if (app == null) continue;

            execProgress.CurrentItem = app.Name;
            progress?.Report(execProgress);

            try
            {
                var (success, error) = await UninstallAppAsync(app, ct);

                if (success)
                {
                    result.BytesFreed += app.TotalSizeBytes;
                    result.ItemsProcessed++;
                    result.SuccessfulItems.Add(app.Name);
                    appRec.Status = RecommendationStatus.Executed;
                    execProgress.BytesFreed += app.TotalSizeBytes;

                    // Record learning
                    await _learningService.RecordAppDecisionAsync(appRec, true);
                }
                else
                {
                    result.ItemsFailed++;
                    result.Errors.Add($"{app.Name}: {error}");
                    appRec.Status = RecommendationStatus.Failed;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to uninstall {App}", app.Name);
                result.ItemsFailed++;
                result.Errors.Add($"{app.Name}: {ex.Message}");
                appRec.Status = RecommendationStatus.Failed;
            }

            execProgress.CompletedItems++;
            progress?.Report(execProgress);
        }

        sw.Stop();
        result.Duration = sw.Elapsed;
        result.Success = result.ItemsFailed == 0;
        result.Message = $"Removed {result.ItemsProcessed} apps, freed ~{FormatBytes(result.BytesFreed)}";

        await _sessionStore.SaveSessionAsync(session);

        _logger.LogInformation(
            "App removal complete: {Processed} succeeded, {Failed} failed",
            result.ItemsProcessed, result.ItemsFailed);

        return result;
    }

    private async Task<(bool Success, string? Error)> UninstallAppAsync(InstalledApp app, CancellationToken ct)
    {
        try
        {
            if (app.Source == AppSource.MicrosoftStore)
            {
                return await UninstallStoreAppAsync(app, ct);
            }
            else
            {
                return await UninstallWin32AppAsync(app, ct);
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string? Error)> UninstallStoreAppAsync(InstalledApp app, CancellationToken ct)
    {
        // Use app name to find and remove Store app
        var script = $"Get-AppxPackage | Where-Object {{ $_.Name -like '*{EscapeForPowerShell(app.Name)}*' }} | Remove-AppxPackage";

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
        {
            return (false, "Failed to start PowerShell");
        }

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            _logger.LogWarning("PowerShell uninstall failed for {App}: {Error}", app.Name, error);
            return (false, string.IsNullOrWhiteSpace(error) ? "Uninstall failed" : error);
        }

        _logger.LogInformation("Successfully uninstalled Store app: {App}", app.Name);
        return (true, null);
    }

    private async Task<(bool Success, string? Error)> UninstallWin32AppAsync(InstalledApp app, CancellationToken ct)
    {
        // Find uninstall string from registry
        var uninstallString = GetUninstallString(app.Name);

        if (string.IsNullOrEmpty(uninstallString))
        {
            return (false, "Uninstall command not found in registry");
        }

        // Parse uninstall string
        string fileName;
        string arguments;

        // Handle MsiExec specially
        if (uninstallString.Contains("MsiExec", StringComparison.OrdinalIgnoreCase))
        {
            fileName = "msiexec.exe";
            // Extract the /I or /X parameter and add quiet flags
            var msiArgs = uninstallString
                .Replace("MsiExec.exe", "", StringComparison.OrdinalIgnoreCase)
                .Replace("msiexec.exe", "", StringComparison.OrdinalIgnoreCase)
                .Replace("/I", "/X", StringComparison.OrdinalIgnoreCase) // Change install to uninstall
                .Trim();
            arguments = $"{msiArgs} /quiet /norestart";
        }
        else if (uninstallString.StartsWith("\""))
        {
            // Quoted path
            var endQuote = uninstallString.IndexOf('"', 1);
            if (endQuote < 0)
            {
                return (false, "Invalid uninstall string format");
            }
            fileName = uninstallString.Substring(1, endQuote - 1);
            arguments = uninstallString.Substring(endQuote + 1).Trim();

            // Add silent flags if not present
            if (!arguments.Contains("/S", StringComparison.OrdinalIgnoreCase) &&
                !arguments.Contains("/silent", StringComparison.OrdinalIgnoreCase) &&
                !arguments.Contains("/quiet", StringComparison.OrdinalIgnoreCase))
            {
                arguments += " /S /silent";
            }
        }
        else
        {
            // Space-separated
            var spaceIndex = uninstallString.IndexOf(' ');
            if (spaceIndex > 0)
            {
                fileName = uninstallString.Substring(0, spaceIndex);
                arguments = uninstallString.Substring(spaceIndex + 1);
            }
            else
            {
                fileName = uninstallString;
                arguments = "/S /silent";
            }
        }

        if (!File.Exists(fileName))
        {
            return (false, $"Uninstaller not found: {fileName}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        _logger.LogInformation("Running uninstall: {File} {Args}", fileName, arguments);

        using var process = Process.Start(psi);
        if (process == null)
        {
            return (false, "Failed to start uninstaller");
        }

        // Wait with timeout
        var timeoutTask = Task.Delay(60000, ct);
        var waitTask = process.WaitForExitAsync(ct);

        if (await Task.WhenAny(waitTask, timeoutTask) == timeoutTask)
        {
            try { process.Kill(); } catch { }
            return (false, "Uninstall timed out");
        }

        // Exit code 0 or 3010 (reboot required) are success
        if (process.ExitCode == 0 || process.ExitCode == 3010)
        {
            _logger.LogInformation("Successfully uninstalled Win32 app: {App}", app.Name);
            return (true, null);
        }

        var error = await process.StandardError.ReadToEndAsync(ct);
        return (false, $"Exit code {process.ExitCode}" + (string.IsNullOrWhiteSpace(error) ? "" : $": {error}"));
    }

    private string? GetUninstallString(string appName)
    {
        // Search common uninstall registry locations
        var registryPaths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var path in registryPaths)
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key == null) continue;

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                try
                {
                    using var subKey = key.OpenSubKey(subKeyName);
                    if (subKey == null) continue;

                    var displayName = subKey.GetValue("DisplayName") as string;
                    if (string.IsNullOrEmpty(displayName)) continue;

                    if (displayName.Contains(appName, StringComparison.OrdinalIgnoreCase))
                    {
                        return subKey.GetValue("UninstallString") as string;
                    }
                }
                catch { }
            }
        }

        // Also check current user
        foreach (var path in registryPaths)
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            if (key == null) continue;

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                try
                {
                    using var subKey = key.OpenSubKey(subKeyName);
                    if (subKey == null) continue;

                    var displayName = subKey.GetValue("DisplayName") as string;
                    if (string.IsNullOrEmpty(displayName)) continue;

                    if (displayName.Contains(appName, StringComparison.OrdinalIgnoreCase))
                    {
                        return subKey.GetValue("UninstallString") as string;
                    }
                }
                catch { }
            }
        }

        return null;
    }

    private static string EscapeForPowerShell(string input)
    {
        return input.Replace("'", "''").Replace("`", "``");
    }

    #endregion

    #region File Relocation

    /// <summary>
    /// Executes approved file relocation recommendations.
    /// </summary>
    public async Task<ExecutionResult> ExecuteRelocationAsync(
        DeepScanSession session,
        IProgress<ExecutionProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new ExecutionResult();
        var execProgress = new ExecutionProgress { Phase = "Relocating files" };

        var approvedRelocations = session.RelocationRecommendations?
            .Where(r => r.Status == RecommendationStatus.Approved)
            .ToList() ?? new();

        execProgress.TotalItems = approvedRelocations.Count;
        progress?.Report(execProgress);

        _logger.LogInformation("Starting relocation for {Count} approved clusters", approvedRelocations.Count);

        foreach (var relocation in approvedRelocations)
        {
            ct.ThrowIfCancellationRequested();

            var cluster = relocation.Cluster;
            if (cluster == null || string.IsNullOrEmpty(relocation.TargetDrive)) continue;

            execProgress.CurrentItem = cluster.Name;
            progress?.Report(execProgress);

            try
            {
                var (success, movedBytes, error) = await RelocateClusterAsync(cluster, relocation.TargetDrive, ct);

                if (success)
                {
                    result.BytesFreed += movedBytes; // Space freed on source drive
                    result.ItemsProcessed++;
                    result.SuccessfulItems.Add(cluster.Name);
                    relocation.Status = RecommendationStatus.Executed;
                    execProgress.BytesFreed += movedBytes;

                    // Record learning
                    await _learningService.RecordRelocationDecisionAsync(relocation, true, relocation.TargetDrive);
                }
                else
                {
                    result.ItemsFailed++;
                    result.Errors.Add($"{cluster.Name}: {error}");
                    relocation.Status = RecommendationStatus.Failed;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to relocate {Cluster}", cluster.Name);
                result.ItemsFailed++;
                result.Errors.Add($"{cluster.Name}: {ex.Message}");
                relocation.Status = RecommendationStatus.Failed;
            }

            execProgress.CompletedItems++;
            progress?.Report(execProgress);
        }

        sw.Stop();
        result.Duration = sw.Elapsed;
        result.Success = result.ItemsFailed == 0;
        result.Message = $"Relocated {result.ItemsProcessed} clusters, moved {FormatBytes(result.BytesFreed)}";

        await _sessionStore.SaveSessionAsync(session);

        _logger.LogInformation(
            "Relocation complete: {Processed} succeeded, {Failed} failed",
            result.ItemsProcessed, result.ItemsFailed);

        return result;
    }

    private async Task<(bool Success, long BytesMoved, string? Error)> RelocateClusterAsync(
        FileCluster cluster,
        string targetDrive,
        CancellationToken ct)
    {
        // Use the stored file list if available (safe relocation)
        // Otherwise fall back to directory enumeration (legacy behavior)
        var filesToMove = cluster.FilePaths?.Where(File.Exists).ToList();

        if (filesToMove == null || filesToMove.Count == 0)
        {
            // Legacy fallback - but only if BasePath is a specific directory, not a drive root
            if (string.IsNullOrEmpty(cluster.BasePath) || !Directory.Exists(cluster.BasePath))
            {
                return (false, 0, "Source path not found");
            }

            // Safety check: don't enumerate from drive root or system folders
            var pathRoot = Path.GetPathRoot(cluster.BasePath);
            if (string.Equals(cluster.BasePath.TrimEnd(Path.DirectorySeparatorChar), pathRoot?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                return (false, 0, "Cannot relocate from drive root - files are too scattered");
            }

            // Check for system/protected folders
            var protectedFolders = new[] { "Windows", "Program Files", "Program Files (x86)", "Config.Msi", "$Recycle.Bin", "System Volume Information" };
            var folderName = new DirectoryInfo(cluster.BasePath).Name;
            if (protectedFolders.Any(p => folderName.Equals(p, StringComparison.OrdinalIgnoreCase)))
            {
                return (false, 0, $"Cannot relocate protected system folder: {folderName}");
            }

            filesToMove = Directory.EnumerateFiles(cluster.BasePath, "*", SearchOption.AllDirectories).ToList();
        }

        if (filesToMove.Count == 0)
        {
            return (false, 0, "No files found to relocate");
        }

        // Determine target path - use cluster name for scattered files, directory name otherwise
        var targetFolderName = !string.IsNullOrEmpty(cluster.BasePath) && Directory.Exists(cluster.BasePath)
            ? new DirectoryInfo(cluster.BasePath).Name
            : cluster.Name.Replace(" ", "_");
        var targetBase = Path.Combine(targetDrive, "Relocated", targetFolderName);

        // Ensure target directory exists
        Directory.CreateDirectory(targetBase);

        long bytesMoved = 0;
        var errors = new List<string>();
        var fileCount = 0;

        // Move only the files in our list
        foreach (var filePath in filesToMove)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // Calculate relative path - use BasePath if valid, otherwise use file's directory
                string relativePath;
                if (!string.IsNullOrEmpty(cluster.BasePath) && filePath.StartsWith(cluster.BasePath, StringComparison.OrdinalIgnoreCase))
                {
                    relativePath = Path.GetRelativePath(cluster.BasePath, filePath);
                }
                else
                {
                    // For scattered files, preserve some directory structure based on file location
                    var fileDir = Path.GetDirectoryName(filePath);
                    var fileName = Path.GetFileName(filePath);
                    var parentDir = fileDir != null ? new DirectoryInfo(fileDir).Name : "";
                    relativePath = Path.Combine(parentDir, fileName);
                }
                var targetPath = Path.Combine(targetBase, relativePath);

                // Ensure subdirectory exists
                var targetDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                // Handle conflicts
                if (File.Exists(targetPath))
                {
                    var targetInfo = new FileInfo(targetPath);
                    var sourceInfo = new FileInfo(filePath);

                    // Skip if same size (likely same file)
                    if (targetInfo.Length == sourceInfo.Length)
                    {
                        File.Delete(filePath);
                        bytesMoved += sourceInfo.Length;
                        fileCount++;
                        continue;
                    }

                    // Rename with timestamp
                    var ext = Path.GetExtension(targetPath);
                    var name = Path.GetFileNameWithoutExtension(targetPath);
                    targetPath = Path.Combine(targetDir!, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
                }

                // Move file
                var fileInfo = new FileInfo(filePath);
                bytesMoved += fileInfo.Length;

                await Task.Run(() => File.Move(filePath, targetPath), ct);
                fileCount++;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to move file {File}", filePath);
                errors.Add(filePath);
            }
        }

        // Try to clean up empty source directories
        try
        {
            CleanupEmptyDirectories(cluster.BasePath);
        }
        catch { /* Ignore */ }

        if (errors.Count > 0 && fileCount == 0)
        {
            return (false, 0, "All files failed to move");
        }

        _logger.LogInformation(
            "Relocated cluster {Name}: {Bytes} moved to {Target}",
            cluster.Name, FormatBytes(bytesMoved), targetBase);

        return (true, bytesMoved, errors.Count > 0 ? $"{errors.Count} files failed" : null);
    }

    private void CleanupEmptyDirectories(string path)
    {
        if (!Directory.Exists(path)) return;

        foreach (var dir in Directory.GetDirectories(path))
        {
            CleanupEmptyDirectories(dir);
        }

        if (!Directory.EnumerateFileSystemEntries(path).Any())
        {
            try { Directory.Delete(path); } catch { }
        }
    }

    #endregion

    #region Duplicate Removal

    /// <summary>
    /// Executes removal of duplicate files, keeping one copy of each.
    /// </summary>
    public async Task<ExecutionResult> ExecuteDuplicateRemovalAsync(
        DeepScanSession session,
        IProgress<ExecutionProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new ExecutionResult();
        var execProgress = new ExecutionProgress { Phase = "Removing duplicates" };

        var duplicateGroups = session.DuplicateGroups ?? new();

        // Count total duplicates to remove (all but one from each group)
        var totalToRemove = duplicateGroups.Sum(g => Math.Max(0, g.Files.Count - 1));
        execProgress.TotalItems = totalToRemove;
        progress?.Report(execProgress);

        _logger.LogInformation("Starting duplicate removal for {Count} groups ({Total} files)",
            duplicateGroups.Count, totalToRemove);

        foreach (var group in duplicateGroups)
        {
            ct.ThrowIfCancellationRequested();

            if (group.Files.Count <= 1) continue;

            // Keep the oldest file (most likely the original) based on LastModified
            var sortedFiles = group.Files.OrderBy(f => f.LastModified).ToList();
            var keepFile = sortedFiles.First();
            var deleteFiles = sortedFiles.Skip(1).ToList();

            foreach (var file in deleteFiles)
            {
                ct.ThrowIfCancellationRequested();

                execProgress.CurrentItem = file.Path;
                progress?.Report(execProgress);

                try
                {
                    if (File.Exists(file.Path))
                    {
                        var fileInfo = new FileInfo(file.Path);
                        var fileSize = fileInfo.Length;

                        File.Delete(file.Path);

                        result.BytesFreed += fileSize;
                        result.ItemsProcessed++;
                        result.SuccessfulItems.Add(file.Path);
                        execProgress.BytesFreed += fileSize;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to delete duplicate {File}", file.Path);
                    result.ItemsFailed++;
                    result.Errors.Add($"{file.Path}: {ex.Message}");
                }

                execProgress.CompletedItems++;
                progress?.Report(execProgress);
            }
        }

        sw.Stop();
        result.Duration = sw.Elapsed;
        result.Success = result.ItemsFailed == 0;
        result.Message = $"Removed {result.ItemsProcessed} duplicates, freed {FormatBytes(result.BytesFreed)}";

        // Clear duplicates from session after processing
        session.DuplicateGroups?.Clear();
        await _sessionStore.SaveSessionAsync(session);

        _logger.LogInformation(
            "Duplicate removal complete: {Processed} deleted, {Failed} failed, {Bytes} freed",
            result.ItemsProcessed, result.ItemsFailed, FormatBytes(result.BytesFreed));

        return result;
    }

    #endregion

    #region Batch Execution

    /// <summary>
    /// Executes all approved items across all categories.
    /// </summary>
    public async Task<ExecutionResult> ExecuteAllApprovedAsync(
        DeepScanSession session,
        IProgress<ExecutionProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var combinedResult = new ExecutionResult();
        var execProgress = new ExecutionProgress { Phase = "Executing all approved items" };

        // Count all approved items
        var approvedCleanups = session.CleanupOpportunities?.Count(o => o.Status == RecommendationStatus.Approved) ?? 0;
        var approvedApps = session.AppRemovalRecommendations?.Count(r => r.Status == RecommendationStatus.Approved) ?? 0;
        var approvedRelocations = session.RelocationRecommendations?.Count(r => r.Status == RecommendationStatus.Approved) ?? 0;
        var duplicates = session.DuplicateGroups?.Sum(g => Math.Max(0, g.Files.Count - 1)) ?? 0;

        execProgress.TotalItems = approvedCleanups + approvedApps + approvedRelocations + duplicates;
        progress?.Report(execProgress);

        _logger.LogInformation(
            "Starting batch execution: {Cleanups} cleanups, {Apps} apps, {Relocations} relocations, {Duplicates} duplicates",
            approvedCleanups, approvedApps, approvedRelocations, duplicates);

        // Execute cleanups first (safest)
        if (approvedCleanups > 0)
        {
            execProgress.Phase = "Cleaning up files";
            progress?.Report(execProgress);

            var cleanupResult = await ExecuteCleanupAsync(session, null, ct);
            MergeResults(combinedResult, cleanupResult);
            execProgress.CompletedItems += cleanupResult.ItemsProcessed + cleanupResult.ItemsFailed;
            execProgress.BytesFreed = combinedResult.BytesFreed;
            progress?.Report(execProgress);
        }

        // Execute duplicate removal
        if (duplicates > 0)
        {
            execProgress.Phase = "Removing duplicates";
            progress?.Report(execProgress);

            var dupResult = await ExecuteDuplicateRemovalAsync(session, null, ct);
            MergeResults(combinedResult, dupResult);
            execProgress.CompletedItems += dupResult.ItemsProcessed + dupResult.ItemsFailed;
            execProgress.BytesFreed = combinedResult.BytesFreed;
            progress?.Report(execProgress);
        }

        // Execute relocations
        if (approvedRelocations > 0)
        {
            execProgress.Phase = "Relocating files";
            progress?.Report(execProgress);

            var relocResult = await ExecuteRelocationAsync(session, null, ct);
            MergeResults(combinedResult, relocResult);
            execProgress.CompletedItems += relocResult.ItemsProcessed + relocResult.ItemsFailed;
            execProgress.BytesFreed = combinedResult.BytesFreed;
            progress?.Report(execProgress);
        }

        // Execute app removals last (most impactful)
        if (approvedApps > 0)
        {
            execProgress.Phase = "Removing applications";
            progress?.Report(execProgress);

            var appResult = await ExecuteAppRemovalAsync(session, null, ct);
            MergeResults(combinedResult, appResult);
            execProgress.CompletedItems += appResult.ItemsProcessed + appResult.ItemsFailed;
            execProgress.BytesFreed = combinedResult.BytesFreed;
            progress?.Report(execProgress);
        }

        sw.Stop();
        combinedResult.Duration = sw.Elapsed;
        combinedResult.Success = combinedResult.ItemsFailed == 0;
        combinedResult.Message = $"Completed {combinedResult.ItemsProcessed} actions, freed {FormatBytes(combinedResult.BytesFreed)}";

        // Update session state
        session.State = DeepScanState.Completed;
        await _sessionStore.SaveSessionAsync(session);

        _logger.LogInformation(
            "Batch execution complete: {Processed} succeeded, {Failed} failed, {Bytes} freed in {Duration:F1}s",
            combinedResult.ItemsProcessed, combinedResult.ItemsFailed,
            FormatBytes(combinedResult.BytesFreed), sw.Elapsed.TotalSeconds);

        return combinedResult;
    }

    /// <summary>
    /// Approves all items that meet the safety threshold.
    /// </summary>
    public async Task<int> ApproveAllSafeAsync(DeepScanSession session, double minConfidence = 0.8)
    {
        int approvedCount = 0;

        // Approve high-confidence app removals
        if (session.AppRemovalRecommendations != null)
        {
            foreach (var rec in session.AppRemovalRecommendations)
            {
                if (rec.Status == RecommendationStatus.Pending &&
                    rec.ShouldRemove &&
                    rec.Confidence >= minConfidence)
                {
                    rec.Status = RecommendationStatus.Approved;
                    approvedCount++;
                }
            }
        }

        // Approve high-confidence relocations
        if (session.RelocationRecommendations != null)
        {
            foreach (var rec in session.RelocationRecommendations)
            {
                if (rec.Status == RecommendationStatus.Pending &&
                    rec.ShouldRelocate &&
                    rec.Confidence >= minConfidence)
                {
                    rec.Status = RecommendationStatus.Approved;
                    approvedCount++;
                }
            }
        }

        // Approve low-risk cleanup opportunities
        if (session.CleanupOpportunities != null)
        {
            foreach (var opp in session.CleanupOpportunities)
            {
                if (opp.Status == RecommendationStatus.Pending &&
                    opp.Risk <= CleanupRisk.Low &&
                    opp.Confidence >= minConfidence)
                {
                    opp.Status = RecommendationStatus.Approved;
                    approvedCount++;
                }
            }
        }

        await _sessionStore.SaveSessionAsync(session);

        _logger.LogInformation("Auto-approved {Count} safe items with confidence >= {Confidence:P0}",
            approvedCount, minConfidence);

        return approvedCount;
    }

    private void MergeResults(ExecutionResult target, ExecutionResult source)
    {
        target.BytesFreed += source.BytesFreed;
        target.ItemsProcessed += source.ItemsProcessed;
        target.ItemsFailed += source.ItemsFailed;
        target.Errors.AddRange(source.Errors);
        target.SuccessfulItems.AddRange(source.SuccessfulItems);
    }

    #endregion

    #region Helpers

    private long GetDirectorySize(DirectoryInfo dirInfo)
    {
        long size = 0;
        try
        {
            foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try { size += file.Length; } catch { }
            }
        }
        catch { }
        return size;
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

    #endregion
}
