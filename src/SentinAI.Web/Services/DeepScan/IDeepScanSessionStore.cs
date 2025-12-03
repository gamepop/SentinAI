using System.Text.Json;
using Microsoft.Extensions.Logging;
using SentinAI.Shared.Models.DeepScan;

namespace SentinAI.Web.Services.DeepScan;

/// <summary>
/// Interface for persisting deep scan sessions.
/// </summary>
public interface IDeepScanSessionStore
{
    /// <summary>
    /// Saves a session to persistent storage.
    /// </summary>
    Task SaveSessionAsync(DeepScanSession session);

    /// <summary>
    /// Loads a session by ID.
    /// </summary>
    Task<DeepScanSession?> LoadSessionAsync(Guid sessionId);

    /// <summary>
    /// Gets the most recent session (completed or in progress).
    /// </summary>
    Task<DeepScanSession?> GetLatestSessionAsync();

    /// <summary>
    /// Gets all stored sessions, ordered by date descending.
    /// </summary>
    Task<List<DeepScanSessionSummary>> GetSessionHistoryAsync(int limit = 10);

    /// <summary>
    /// Deletes a session from storage.
    /// </summary>
    Task DeleteSessionAsync(Guid sessionId);

    /// <summary>
    /// Cleans up old sessions beyond retention period.
    /// </summary>
    Task CleanupOldSessionsAsync(int keepCount = 5);
}

/// <summary>
/// Summary info for session history listing.
/// </summary>
public class DeepScanSessionSummary
{
    public Guid Id { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DeepScanState State { get; set; }
    public int TotalRecommendations { get; set; }
    public long PotentialSpaceSavings { get; set; }
    public List<string> TargetDrives { get; set; } = new();
}

/// <summary>
/// File-based implementation of the session store.
/// Stores sessions as JSON files in the app data directory.
/// </summary>
public class FileBasedDeepScanSessionStore : IDeepScanSessionStore
{
    private readonly ILogger<FileBasedDeepScanSessionStore> _logger;
    private readonly string _storageDir;
    private readonly JsonSerializerOptions _jsonOptions;

    public FileBasedDeepScanSessionStore(ILogger<FileBasedDeepScanSessionStore> logger)
    {
        _logger = logger;
        _storageDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SentinAI",
            "DeepScanSessions");

        Directory.CreateDirectory(_storageDir);

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    public async Task SaveSessionAsync(DeepScanSession session)
    {
        try
        {
            var filePath = GetSessionFilePath(session.Id);
            var json = JsonSerializer.Serialize(session, _jsonOptions);
            await File.WriteAllTextAsync(filePath, json);
            _logger.LogDebug("Saved deep scan session {SessionId} to {Path}", session.Id, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save deep scan session {SessionId}", session.Id);
            throw;
        }
    }

    public async Task<DeepScanSession?> LoadSessionAsync(Guid sessionId)
    {
        try
        {
            var filePath = GetSessionFilePath(sessionId);
            if (!File.Exists(filePath))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(filePath);
            return JsonSerializer.Deserialize<DeepScanSession>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load deep scan session {SessionId}", sessionId);
            return null;
        }
    }

    public async Task<DeepScanSession?> GetLatestSessionAsync()
    {
        try
        {
            var files = Directory.GetFiles(_storageDir, "*.json")
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .FirstOrDefault();

            if (files == null)
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(files);
            return JsonSerializer.Deserialize<DeepScanSession>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get latest deep scan session");
            return null;
        }
    }

    public async Task<List<DeepScanSessionSummary>> GetSessionHistoryAsync(int limit = 10)
    {
        var summaries = new List<DeepScanSessionSummary>();

        try
        {
            var files = Directory.GetFiles(_storageDir, "*.json")
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .Take(limit);

            foreach (var file in files)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var session = JsonSerializer.Deserialize<DeepScanSession>(json, _jsonOptions);
                    if (session != null)
                    {
                        summaries.Add(new DeepScanSessionSummary
                        {
                            Id = session.Id,
                            StartedAt = session.StartedAt,
                            CompletedAt = session.CompletedAt,
                            State = session.State,
                            TotalRecommendations = session.Summary?.TotalRecommendations ?? 0,
                            PotentialSpaceSavings = session.Summary?.PotentialSpaceSavings ?? 0
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read session file {File}", file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get session history");
        }

        return summaries;
    }

    public Task DeleteSessionAsync(Guid sessionId)
    {
        try
        {
            var filePath = GetSessionFilePath(sessionId);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger.LogInformation("Deleted deep scan session {SessionId}", sessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete deep scan session {SessionId}", sessionId);
        }

        return Task.CompletedTask;
    }

    public Task CleanupOldSessionsAsync(int keepCount = 5)
    {
        try
        {
            var files = Directory.GetFiles(_storageDir, "*.json")
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .Skip(keepCount)
                .ToList();

            foreach (var file in files)
            {
                try
                {
                    File.Delete(file);
                    _logger.LogDebug("Cleaned up old session file {File}", file);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete old session file {File}", file);
                }
            }

            if (files.Count > 0)
            {
                _logger.LogInformation("Cleaned up {Count} old deep scan sessions", files.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup old sessions");
        }

        return Task.CompletedTask;
    }

    private string GetSessionFilePath(Guid sessionId)
    {
        return Path.Combine(_storageDir, $"{sessionId}.json");
    }
}
