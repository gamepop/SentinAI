using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using SentinAI.Web.Services;

namespace SentinAI.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DuplicatesController : ControllerBase
{
    private readonly IDuplicateFileService _duplicateService;
    private readonly ILogger<DuplicatesController> _logger;

    public DuplicatesController(
        IDuplicateFileService duplicateService,
        ILogger<DuplicatesController> logger)
    {
        _duplicateService = duplicateService;
        _logger = logger;
    }

    /// <summary>
    /// Start a duplicate file scan
    /// </summary>
    [HttpPost("scan")]
    public async Task<ActionResult<DuplicateScanResponse>> StartScan([FromBody] DuplicateScanRequest request)
    {
        var rootPath = ResolveRootPath(request);
        if (!Directory.Exists(rootPath))
        {
            return BadRequest(new { error = $"Scan path '{rootPath}' does not exist." });
        }

        _logger.LogInformation("Starting duplicate scan for {Path}", rootPath);

        var options = new DuplicateScanOptions
        {
            MinFileSizeBytes = request.MinFileSizeBytes ?? 1024,
            MaxFileSizeBytes = request.MaxFileSizeBytes ?? 10L * 1024 * 1024 * 1024,
            ExcludedExtensions = request.ExcludedExtensions ?? new[] { ".sys", ".dll", ".exe" },
            ExcludedFolders = request.ExcludedFolders ?? new[] { "Windows", "Program Files", "$Recycle.Bin" },
            IncludeHiddenFiles = request.IncludeHiddenFiles ?? false,
            IncludeSubdirectories = request.IncludeSubdirectories ?? true
        };

        try
        {
            var result = await _duplicateService.ScanForDuplicatesAsync(
                rootPath,
                options,
                cancellationToken: HttpContext.RequestAborted);

            var response = new DuplicateScanResponse(
                DuplicateGroups: result.DuplicateGroups.Count,
                TotalDuplicateSize: result.TotalWastedBytes,
                FilesScanned: result.TotalFilesScanned,
                RootPath: result.RootPath);

            return Ok(response);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499, new { error = "Scan cancelled" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Duplicate scan failed");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get cached duplicate groups from last scan
    /// </summary>
    [HttpGet("groups")]
    public async Task<ActionResult<DuplicateGroupsResponse>> GetDuplicateGroups()
    {
        var groups = await _duplicateService.GetDuplicateGroupsAsync();
        var rootPath = await _duplicateService.GetLastScanRootPathAsync();
        var responseGroups = groups
            .OrderByDescending(g => g.WastedBytes)
            .Select(g => new DuplicateGroupDto(
                Hash: g.Hash,
                FileSize: g.FileSize,
                Files: g.Files
                    .OrderBy(f => f.LastModified)
                    .Select(f => new DuplicateFileDto(
                        Path: f.FilePath,
                        ModifiedDate: ToDateTimeOffset(f.LastModified)))
                    .ToList()))
            .ToList();

        var response = new DuplicateGroupsResponse(
            Groups: responseGroups,
            TotalRecoverableSize: groups.Sum(g => g.WastedBytes),
            RootPath: rootPath);

        return Ok(response);
    }

    /// <summary>
    /// Clear duplicate cache
    /// </summary>
    [HttpDelete("cache")]
    public ActionResult ClearCache()
    {
        _duplicateService.ClearCache();
        return Ok(new { message = "Cache cleared" });
    }

    /// <summary>
    /// Delete selected duplicate files
    /// </summary>
    [HttpPost("delete")]
    public async Task<ActionResult<DeleteDuplicatesResponse>> DeleteDuplicates([FromBody] DeleteDuplicatesRequest request)
    {
        if (request.FilePaths == null || request.FilePaths.Count == 0)
        {
            return BadRequest(new { error = "No file paths were provided" });
        }

        try
        {
            var result = await _duplicateService.DeleteFilesAsync(
                request.FilePaths,
                cancellationToken: HttpContext.RequestAborted);

            var response = new DeleteDuplicatesResponse(
                DeletedCount: result.DeletedCount,
                BytesFreed: result.BytesFreed,
                Errors: result.Errors);

            return Ok(response);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499, new { error = "Delete cancelled" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete duplicate files");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    private static string ResolveRootPath(DuplicateScanRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.RootPath))
        {
            return request.RootPath!;
        }

        if (!string.IsNullOrWhiteSpace(request.Path))
        {
            return request.Path!;
        }

        return "C:\\Users";
    }

    private static DateTimeOffset ToDateTimeOffset(DateTime value)
    {
        if (value.Kind == DateTimeKind.Unspecified)
        {
            value = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }
        return new DateTimeOffset(value);
    }
}

public class DuplicateScanRequest
{
    public string? RootPath { get; set; }
    public string? Path { get; set; }
    public long? MinFileSizeBytes { get; set; }
    public long? MaxFileSizeBytes { get; set; }
    public string[]? ExcludedExtensions { get; set; }
    public string[]? ExcludedFolders { get; set; }
    public bool? IncludeHiddenFiles { get; set; }
    public bool? IncludeSubdirectories { get; set; }
}

public record DuplicateScanResponse(int DuplicateGroups, long TotalDuplicateSize, int FilesScanned, string RootPath);

public record DuplicateGroupsResponse(List<DuplicateGroupDto> Groups, long TotalRecoverableSize, string? RootPath = null);

public record DuplicateGroupDto(string Hash, long FileSize, List<DuplicateFileDto> Files);

public record DuplicateFileDto(string Path, DateTimeOffset ModifiedDate);

public class DeleteDuplicatesRequest
{
    public List<string> FilePaths { get; set; } = new();
}

public record DeleteDuplicatesResponse(int DeletedCount, long BytesFreed, List<string> Errors);
