using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SentinAI.Shared.Models.DeepScan;

namespace SentinAI.Web.Services.DeepScan;

/// <summary>
/// Orchestrates the deep scan process across all phases.
/// </summary>
public class DeepScanService
{
    private readonly ILogger<DeepScanService> _logger;
    private readonly FileSystemScanner _fileScanner;
    private readonly AppDiscoveryService _appDiscovery;
    private readonly SpaceAnalysisService _spaceAnalysis;
    private readonly DriveManagerService _driveManager;
    private readonly DeepScanBrainAnalyzer _brainAnalyzer;
    private readonly IDeepScanRagStore _ragStore;
    
    private readonly ConcurrentDictionary<string, DeepScanSession> _sessions = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new();
    
    public DeepScanService(
        ILogger<DeepScanService> logger,
        FileSystemScanner fileScanner,
        AppDiscoveryService appDiscovery,
        SpaceAnalysisService spaceAnalysis,
        DriveManagerService driveManager,
        DeepScanBrainAnalyzer brainAnalyzer,
        IDeepScanRagStore ragStore)
    {
        _logger = logger;
        _fileScanner = fileScanner;
        _appDiscovery = appDiscovery;
        _spaceAnalysis = spaceAnalysis;
        _driveManager = driveManager;
        _brainAnalyzer = brainAnalyzer;
        _ragStore = ragStore;
    }
    
    /// <summary>
    /// Starts a new deep scan session.
    /// </summary>
    public async Task<DeepScanSession> StartDeepScanAsync(DeepScanOptions? options = null)
    {
        options ??= new DeepScanOptions();
        
        var session = new DeepScanSession
        {
            Options = options,
            State = DeepScanState.Initializing
        };
        
        _sessions[session.Id] = session;
        var cts = new CancellationTokenSource();
        _cancellations[session.Id] = cts;
        
        _logger.LogInformation("Starting deep scan session {SessionId}", session.Id);
        
        // Run scan in background
        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteScanAsync(session, cts.Token);
            }
            catch (OperationCanceledException)
            {
                session.State = DeepScanState.Cancelled;
                _logger.LogInformation("Deep scan {SessionId} cancelled", session.Id);
            }
            catch (Exception ex)
            {
                session.State = DeepScanState.Failed;
                _logger.LogError(ex, "Deep scan {SessionId} failed", session.Id);
            }
            finally
            {
                session.EndTime = DateTime.UtcNow;
            }
        });
        
        return session;
    }
    
    /// <summary>
    /// Cancels an active scan.
    /// </summary>
    public Task CancelScanAsync(string sessionId)
    {
        if (_cancellations.TryGetValue(sessionId, out var cts))
        {
            cts.Cancel();
            _logger.LogInformation("Cancellation requested for session {SessionId}", sessionId);
        }
        return Task.CompletedTask;
    }
    
    /// <summary>
    /// Gets a session by ID.
    /// </summary>
    public DeepScanSession? GetSession(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }
    
    /// <summary>
    /// Gets progress for a session.
    /// </summary>
    public DeepScanProgress? GetProgress(string sessionId)
    {
        return GetSession(sessionId)?.Progress;
    }
    
    /// <summary>
    /// Gets all active sessions.
    /// </summary>
    public IEnumerable<DeepScanSession> GetActiveSessions()
    {
        return _sessions.Values.Where(s => 
            s.State != DeepScanState.Completed && 
            s.State != DeepScanState.Cancelled && 
            s.State != DeepScanState.Failed);
    }
    
    private async Task ExecuteScanAsync(DeepScanSession session, CancellationToken ct)
    {
        var progress = session.Progress;
        progress.TotalPhases = 6;
        
        // Phase 1: Analyze drives
        progress.CurrentPhase = "Analyzing Drives";
        progress.CurrentPhaseNumber = 1;
        session.State = DeepScanState.ScanningDrives;
        
        foreach (var drivePath in session.Options.TargetDrives)
        {
            ct.ThrowIfCancellationRequested();
            var driveAnalysis = await _driveManager.AnalyzeDriveAsync(drivePath, ct);
            session.DriveAnalyses.Add(driveAnalysis);
            progress.PhaseProgress = 100;
        }
        UpdateOverallProgress(progress);
        
        // Phase 2: Discover installed apps
        if (session.Options.ScanInstalledApps)
        {
            progress.CurrentPhase = "Discovering Applications";
            progress.CurrentPhaseNumber = 2;
            session.State = DeepScanState.DiscoveringApps;
            
            await foreach (var app in _appDiscovery.DiscoverAppsAsync(ct))
            {
                session.DiscoveredApps.Add(app);
                progress.AppsDiscovered = session.DiscoveredApps.Count;
                progress.PhaseProgress = Math.Min(progress.AppsDiscovered * 2, 100);
            }
        }
        UpdateOverallProgress(progress);
        
        // Phase 3: Scan file system
        progress.CurrentPhase = "Scanning Files";
        progress.CurrentPhaseNumber = 3;
        session.State = DeepScanState.AnalyzingFiles;
        
        foreach (var drivePath in session.Options.TargetDrives)
        {
            ct.ThrowIfCancellationRequested();
            await _fileScanner.ScanDriveAsync(
                drivePath, 
                session.Options,
                (scanned, bytes, path) =>
                {
                    progress.FilesScanned = scanned;
                    progress.BytesAnalyzed = bytes;
                    progress.CurrentPath = path;
                    progress.LastUpdate = DateTime.UtcNow;
                },
                ct);
        }
        UpdateOverallProgress(progress);
        
        // Phase 4: Cluster files
        progress.CurrentPhase = "Clustering Files";
        progress.CurrentPhaseNumber = 4;
        session.State = DeepScanState.ClusteringFiles;
        
        var clusters = await _spaceAnalysis.ClusterFilesAsync(
            _fileScanner.GetScannedFiles(), 
            session.DiscoveredApps,
            ct);
        session.FileClusters = clusters;
        UpdateOverallProgress(progress);
        
        // Phase 5: Generate AI recommendations with RAG
        progress.CurrentPhase = "Generating AI Recommendations";
        progress.CurrentPhaseNumber = 5;
        session.State = DeepScanState.GeneratingRecommendations;
        
        // App removal recommendations
        foreach (var app in session.DiscoveredApps.Where(a => a.IsUnused || a.IsBloatware))
        {
            ct.ThrowIfCancellationRequested();
            var recommendation = await _brainAnalyzer.AnalyzeAppForRemovalAsync(app, ct);
            session.AppRemovalRecommendations.Add(recommendation);
            progress.RecommendationsGenerated++;
        }
        
        // File relocation recommendations
        foreach (var cluster in clusters.Where(c => c.TotalBytes > 100 * 1024 * 1024)) // >100MB
        {
            ct.ThrowIfCancellationRequested();
            var recommendation = await _brainAnalyzer.AnalyzeFilesForRelocationAsync(cluster, ct);
            if (recommendation.ShouldRelocate)
            {
                session.RelocationRecommendations.Add(recommendation);
                progress.RecommendationsGenerated++;
            }
        }
        
        // Cleanup opportunities
        var cleanupOpportunities = await _spaceAnalysis.FindCleanupOpportunitiesAsync(
            session.DriveAnalyses, 
            session.DiscoveredApps,
            ct);
        
        foreach (var opportunity in cleanupOpportunities)
        {
            ct.ThrowIfCancellationRequested();
            var analyzed = await _brainAnalyzer.AnalyzeCleanupOpportunityAsync(opportunity, ct);
            session.CleanupOpportunities.Add(analyzed);
            progress.RecommendationsGenerated++;
        }
        
        UpdateOverallProgress(progress);
        
        // Phase 6: Generate summary
        progress.CurrentPhase = "Generating Summary";
        progress.CurrentPhaseNumber = 6;
        
        session.Summary = GenerateSummary(session);
        
        progress.PhaseProgress = 100;
        progress.OverallProgress = 100;
        session.State = DeepScanState.AwaitingApproval;
        session.EndTime = DateTime.UtcNow;
        
        _logger.LogInformation(
            "Deep scan {SessionId} completed. Found {Apps} apps, {Recommendations} recommendations, potential savings: {Savings}",
            session.Id,
            session.DiscoveredApps.Count,
            session.Summary.TotalRecommendations,
            session.Summary.PotentialSpaceSavingsFormatted);
    }
    
    private void UpdateOverallProgress(DeepScanProgress progress)
    {
        var phaseWeight = 100.0 / progress.TotalPhases;
        var completedPhases = progress.CurrentPhaseNumber - 1;
        var currentPhaseContribution = (progress.PhaseProgress / 100.0) * phaseWeight;
        progress.OverallProgress = (completedPhases * phaseWeight) + currentPhaseContribution;
        progress.LastUpdate = DateTime.UtcNow;
    }
    
    private DeepScanSummary GenerateSummary(DeepScanSession session)
    {
        return new DeepScanSummary
        {
            TotalBytesScanned = session.Progress.BytesAnalyzed,
            TotalFilesScanned = session.Progress.FilesScanned,
            TotalAppsFound = session.DiscoveredApps.Count,
            TotalRecommendations = 
                session.AppRemovalRecommendations.Count + 
                session.RelocationRecommendations.Count + 
                session.CleanupOpportunities.Count,
            PotentialSpaceSavings = 
                session.AppRemovalRecommendations.Sum(r => r.TotalPotentialSavings) +
                session.CleanupOpportunities.Sum(c => c.Bytes),
            AppsRecommendedForRemoval = session.AppRemovalRecommendations.Count(r => r.ShouldRemove),
            FileClustersForRelocation = session.RelocationRecommendations.Count,
            CacheCleanupOpportunities = session.CleanupOpportunities.Count(c => 
                c.Type == CleanupType.BrowserCache || 
                c.Type == CleanupType.AppCache ||
                c.Type == CleanupType.ThumbnailCache),
            DuplicateGroupsFound = session.FileClusters.Count(c => c.Type == FileClusterType.Duplicates)
        };
    }
}
