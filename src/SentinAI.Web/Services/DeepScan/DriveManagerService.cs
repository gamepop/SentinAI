using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using SentinAI.Shared.Models.DeepScan;

namespace SentinAI.Web.Services.DeepScan;

/// <summary>
/// Service for managing and discovering drives on the system.
/// </summary>
[SupportedOSPlatform("windows")]
public class DriveManagerService
{
    private readonly ILogger<DriveManagerService> _logger;

    public DriveManagerService(ILogger<DriveManagerService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets information about all fixed drives on the system.
    /// </summary>
    public Task<List<TargetDriveInfo>> GetAvailableDrivesAsync()
    {
        var drives = new List<TargetDriveInfo>();

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                {
                    drives.Add(new TargetDriveInfo
                    {
                        Letter = drive.Name,
                        Label = string.IsNullOrEmpty(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel,
                        TotalSpace = drive.TotalSize,
                        FreeSpace = drive.AvailableFreeSpace,
                        DriveType = drive.DriveType.ToString(),
                        IsReady = drive.IsReady
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting drive information");
        }

        _logger.LogInformation("Found {Count} available drives", drives.Count);
        return Task.FromResult(drives);
    }

    /// <summary>
    /// Gets detailed information about a specific drive.
    /// </summary>
    public Task<TargetDriveInfo?> GetDriveInfoAsync(string driveLetter)
    {
        try
        {
            // Normalize drive letter format (e.g., "C" -> "C:\")
            if (!driveLetter.EndsWith(":\\"))
            {
                driveLetter = driveLetter.TrimEnd(':', '\\') + ":\\";
            }

            var drive = new DriveInfo(driveLetter);
            if (drive.IsReady)
            {
                return Task.FromResult<TargetDriveInfo?>(new TargetDriveInfo
                {
                    Letter = drive.Name,
                    Label = string.IsNullOrEmpty(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel,
                    TotalSpace = drive.TotalSize,
                    FreeSpace = drive.AvailableFreeSpace,
                    DriveType = drive.DriveType.ToString(),
                    IsReady = drive.IsReady
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting info for drive {Drive}", driveLetter);
        }

        return Task.FromResult<TargetDriveInfo?>(null);
    }

    /// <summary>
    /// Gets drives that have enough free space for relocation.
    /// </summary>
    public async Task<List<AvailableDrive>> GetDrivesForRelocationAsync(long requiredSpace, string excludeDrive)
    {
        var availableDrives = new List<AvailableDrive>();
        var drives = await GetAvailableDrivesAsync();

        foreach (var drive in drives)
        {
            // Skip the source drive
            if (drive.Letter.StartsWith(excludeDrive, StringComparison.OrdinalIgnoreCase))
                continue;

            // Check if drive has enough space (with 10% buffer)
            var requiredWithBuffer = (long)(requiredSpace * 1.1);
            if (drive.FreeSpace >= requiredWithBuffer)
            {
                availableDrives.Add(new AvailableDrive
                {
                    Letter = drive.Letter,
                    Label = drive.Label,
                    FreeSpace = drive.FreeSpace,
                    TotalSpace = drive.TotalSpace,
                    IsRecommended = drive.FreeSpace > requiredSpace * 2 // Recommend if plenty of space
                });
            }
        }

        return availableDrives.OrderByDescending(d => d.FreeSpace).ToList();
    }
}
