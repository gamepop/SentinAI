using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using SentinAI.Shared.Models.DeepScan;
using DeepScanDuplicateGroup = SentinAI.Shared.Models.DeepScan.DuplicateGroup;

namespace SentinAI.Web.Services.DeepScan;

/// <summary>
/// Main orchestrator service for deep scan operations.
/// </summary>
[SupportedOSPlatform("windows")]
public class DeepScanService
{
    private readonly ILogger<DeepScanService> _logger;
    private readonly FileSystemScanner _fileScanner;
    private readonly AppDiscoveryService _appDiscovery;
    private readonly DriveManagerService _driveManager;
    private readonly SpaceAnalysisService _spaceAnalysis;
    private readonly DeepScanBrainAnalyzer _brainAnalyzer;
    private readonly DeepScanLearningService _learningService;
    private readonly IDeepScanSessionStore _sessionStore;

    private readonly Dictionary<Guid, DeepScanSession> _activeSessions = new();
    private readonly object _lock = new();

    public DeepScanService(
        ILogger<DeepScanService> logger,
        FileSystemScanner fileScanner,
        AppDiscoveryService appDiscovery,
        DriveManagerService driveManager,
        SpaceAnalysisService spaceAnalysis,
        DeepScanBrainAnalyzer brainAnalyzer,
        DeepScanLearningService learningService,
        IDeepScanSessionStore sessionStore)
    {
        _logger = logger;
        _fileScanner = fileScanner;
        _appDiscovery = appDiscovery;
        _driveManager = driveManager;
        _spaceAnalysis = spaceAnalysis;
        _brainAnalyzer = brainAnalyzer;
        _learningService = learningService;
        _sessionStore = sessionStore;
    }

    /// <summary>
    /// Starts a new deep scan session.
    /// </summary>
    public async Task<DeepScanSession> StartScanAsync(DeepScanOptions options, CancellationToken ct = default)
    {
        var session = new DeepScanSession
        {
            State = DeepScanState.Initializing
        };

        lock (_lock)
        {
            _activeSessions[session.Id] = session;
        }

        // Persist the session immediately
        await _sessionStore.SaveSessionAsync(session);

        _logger.LogInformation("Starting deep scan session {SessionId}", session.Id);

        // Start scan in background
        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteScanAsync(session, options, ct);
            }
            catch (OperationCanceledException)
            {
                session.State = DeepScanState.Cancelled;
                await _sessionStore.SaveSessionAsync(session);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Deep scan failed for session {SessionId}", session.Id);
                session.State = DeepScanState.Failed;
                session.ErrorMessage = ex.Message;
                await _sessionStore.SaveSessionAsync(session);
            }
            finally
            {
                // Remove from active sessions when done
                lock (_lock)
                {
                    _activeSessions.Remove(session.Id);
                }
            }
        }, ct);

        return session;
    }

    /// <summary>
    /// Gets a scan session by ID (checks active sessions first, then storage).
    /// </summary>
    public async Task<DeepScanSession?> GetSessionAsync(Guid sessionId)
    {
        // Check active sessions first
        lock (_lock)
        {
            if (_activeSessions.TryGetValue(sessionId, out var activeSession))
            {
                return activeSession;
            }
        }

        // Fall back to persistent storage
        return await _sessionStore.LoadSessionAsync(sessionId);
    }

    /// <summary>
    /// Gets a scan session by ID (synchronous version for backward compatibility).
    /// </summary>
    public DeepScanSession? GetSession(Guid sessionId)
    {
        // Check active sessions first
        lock (_lock)
        {
            if (_activeSessions.TryGetValue(sessionId, out var activeSession))
            {
                return activeSession;
            }
        }

        // Fall back to persistent storage (blocking)
        return _sessionStore.LoadSessionAsync(sessionId).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Gets the most recent session.
    /// </summary>
    public async Task<DeepScanSession?> GetLatestSessionAsync()
    {
        // Check if there's an active session
        lock (_lock)
        {
            var activeSession = _activeSessions.Values
                .OrderByDescending(s => s.StartedAt)
                .FirstOrDefault();
            if (activeSession != null)
            {
                return activeSession;
            }
        }

        // Fall back to persistent storage
        return await _sessionStore.GetLatestSessionAsync();
    }

    /// <summary>
    /// Gets session history.
    /// </summary>
    public Task<List<DeepScanSessionSummary>> GetSessionHistoryAsync(int limit = 10)
    {
        return _sessionStore.GetSessionHistoryAsync(limit);
    }

    /// <summary>
    /// Cancels an ongoing scan.
    /// </summary>
    public void CancelScan(Guid sessionId)
    {
        var session = GetSession(sessionId);
        if (session != null)
        {
            session.State = DeepScanState.Cancelled;
        }
    }

    private async Task ExecuteScanAsync(DeepScanSession session, DeepScanOptions options, CancellationToken ct)
    {
        var appRecommendations = new List<AppRemovalRecommendation>();
        var relocationRecommendations = new List<RelocationRecommendation>();
        var cleanupOpportunities = new List<CleanupOpportunity>();
        var duplicateGroups = new List<DeepScanDuplicateGroup>();

        // Phase 1: Scan files
        session.State = DeepScanState.ScanningFiles;
        _fileScanner.ClearCache();

        foreach (var drive in options.TargetDrives)
        {
            ct.ThrowIfCancellationRequested();
            session.Progress.CurrentPhase = $"Scanning {drive}...";

            await _fileScanner.ScanDriveAsync(drive, options, (files, bytes, path) =>
            {
                session.Progress.FilesScanned = files;
                session.Progress.BytesAnalyzed = bytes;
                session.Progress.CurrentPath = path;
                session.Progress.OverallProgress = 20 * (double)bytes / (100L * 1024 * 1024 * 1024); // Estimate 100GB
            }, ct);
        }

        // Phase 2: Scan apps
        if (options.ScanInstalledApps)
        {
            session.State = DeepScanState.ScanningApps;
            session.Progress.CurrentPhase = "Discovering installed applications...";
            session.Progress.OverallProgress = 25;

            var apps = await _appDiscovery.DiscoverAppsAsync(ct);
            session.Progress.AppsDiscovered = apps.Count;

            // Analyze each app with brain
            session.State = DeepScanState.AnalyzingWithAi;
            session.Progress.CurrentPhase = "Analyzing applications with AI...";

            var appIndex = 0;
            foreach (var app in apps)
            {
                ct.ThrowIfCancellationRequested();

                var recommendation = await _brainAnalyzer.AnalyzeAppForRemovalAsync(app, ct);
                appRecommendations.Add(recommendation);

                session.Progress.RecommendationsGenerated++;
                session.Progress.OverallProgress = 25 + (25 * (double)++appIndex / apps.Count);
            }
        }

        // Phase 3: Find duplicates
        if (options.ScanForDuplicates)
        {
            session.State = DeepScanState.FindingDuplicates;
            session.Progress.CurrentPhase = "Finding duplicate files...";
            session.Progress.OverallProgress = 55;

            var scannedFiles = _fileScanner.GetScannedFiles();
            duplicateGroups = await _fileScanner.FindDuplicatesAsync(scannedFiles, ct);
        }

        // Phase 4: Analyze space and generate relocation/cleanup recommendations
        session.State = DeepScanState.GeneratingRecommendations;
        session.Progress.CurrentPhase = "Generating recommendations...";
        session.Progress.OverallProgress = 75;

        // Get cleanup opportunities
        foreach (var drive in options.TargetDrives)
        {
            ct.ThrowIfCancellationRequested();
            var driveOpportunities = await _spaceAnalysis.FindCleanupOpportunitiesAsync(drive, ct);

            foreach (var opp in driveOpportunities)
            {
                var analyzed = await _brainAnalyzer.AnalyzeCleanupOpportunityAsync(opp, ct);
                cleanupOpportunities.Add(analyzed);
            }
        }

        // Get relocation candidates
        if (options.GenerateRelocationPlans)
        {
            var scannedFiles = _fileScanner.GetScannedFiles();
            var availableDrives = await _driveManager.GetDrivesForRelocationAsync(0, options.TargetDrives.First());
            var clusters = await _spaceAnalysis.IdentifyRelocationCandidatesAsync(scannedFiles, availableDrives, ct);

            foreach (var cluster in clusters)
            {
                ct.ThrowIfCancellationRequested();
                var recommendation = await _brainAnalyzer.AnalyzeFilesForRelocationAsync(cluster, ct);
                relocationRecommendations.Add(recommendation);
            }
        }

        // Complete
        session.Progress.OverallProgress = 100;
        session.Progress.CurrentPhase = "Complete";
        session.State = DeepScanState.AwaitingApproval;
        session.CompletedAt = DateTime.UtcNow;

        // Store results
        session.AppRemovalRecommendations = appRecommendations
            .Where(r => r.ShouldRemove || r.Confidence > 0.5)
            .OrderByDescending(r => r.TotalPotentialSavings)
            .ToList();
        session.RelocationRecommendations = relocationRecommendations
            .Where(r => r.ShouldRelocate)
            .OrderByDescending(r => r.Priority)
            .ToList();
        session.CleanupOpportunities = cleanupOpportunities
            .OrderByDescending(o => o.Bytes)
            .ToList();
        session.DuplicateGroups = duplicateGroups;

        // Generate summary
        session.Summary = new DeepScanSummary
        {
            TotalRecommendations = session.AppRemovalRecommendations.Count +
                                   session.RelocationRecommendations.Count +
                                   session.CleanupOpportunities.Count,
            AppsRecommendedForRemoval = session.AppRemovalRecommendations.Count(r => r.ShouldRemove),
            FileClustersToRelocate = session.RelocationRecommendations.Count,
            CleanupOpportunitiesFound = session.CleanupOpportunities.Count,
            DuplicateGroupsFound = duplicateGroups.Count,
            PotentialSpaceSavings = session.AppRemovalRecommendations.Sum(r => r.TotalPotentialSavings) +
                                   session.CleanupOpportunities.Sum(o => o.Bytes) +
                                   duplicateGroups.Sum(g => g.WastedBytes),
            DuplicateSpaceSavings = duplicateGroups.Sum(g => g.WastedBytes)
        };

        // Persist the completed session
        await _sessionStore.SaveSessionAsync(session);

        // Cleanup old sessions (keep last 5)
        await _sessionStore.CleanupOldSessionsAsync(5);

        _logger.LogInformation(
            "Deep scan complete. {Recommendations} recommendations, {Savings} potential savings",
            session.Summary.TotalRecommendations,
            session.Summary.PotentialSpaceSavingsFormatted);
    }

    /// <summary>
    /// Records user feedback for an app removal recommendation.
    /// </summary>
    public async Task RecordAppFeedbackAsync(AppRemovalRecommendation recommendation, bool approved)
    {
        await _learningService.RecordAppDecisionAsync(recommendation, approved);
    }

    /// <summary>
    /// Records user feedback for a relocation recommendation.
    /// </summary>
    public async Task RecordRelocationFeedbackAsync(RelocationRecommendation recommendation, bool approved, string? actualDrive = null)
    {
        await _learningService.RecordRelocationDecisionAsync(recommendation, approved, actualDrive);
    }

    /// <summary>
    /// Records user feedback for a cleanup opportunity.
    /// </summary>
    public async Task RecordCleanupFeedbackAsync(CleanupOpportunity opportunity, bool approved)
    {
        await _learningService.RecordCleanupDecisionAsync(opportunity, approved);
    }
}
