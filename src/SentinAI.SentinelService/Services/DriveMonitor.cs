using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using SentinAI.Shared.Models;
using SentinAI.Shared.Services;
using Microsoft.Extensions.Logging;

namespace SentinAI.SentinelService.Services;

/// <summary>
/// The "Eyes" - Monitors filesystem changes using USN Journal
/// Implements debouncing to prevent reaction to rapid changes
/// </summary>
public class DriveMonitor : BackgroundService
{
    private readonly ILogger<DriveMonitor> _logger;
    private readonly IUsnJournalReader _journalReader;
    private readonly IServiceProvider _serviceProvider;
    private readonly IMonitoringActivityPublisher _activityPublisher;
    private readonly ISubject<UsnJournalEntry> _fileEvents;
    private readonly string _driveLetter;

    // Thresholds
    private const int DEBOUNCE_SECONDS = 30;
    private const int BATCH_SIZE = 1000;
    private const long HEAVY_WRITE_THRESHOLD = 500 * 1024 * 1024; // 500MB

    public DriveMonitor(
        ILogger<DriveMonitor> logger,
        IUsnJournalReader journalReader,
        IServiceProvider serviceProvider,
        IMonitoringActivityPublisher activityPublisher)
    {
        _logger = logger;
        _journalReader = journalReader;
        _serviceProvider = serviceProvider;
        _activityPublisher = activityPublisher;
        _fileEvents = new Subject<UsnJournalEntry>();
        _driveLetter = "C:\\"; // TODO: Make configurable
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Drive Monitor starting for drive: {Drive}", _driveLetter);

        await _activityPublisher.PublishAsync(new MonitoringActivity
        {
            Type = MonitoringActivityType.DriveSweep,
            Scope = _driveLetter,
            Drive = _driveLetter,
            State = "Starting",
            Message = "Drive monitor initialized"
        }, stoppingToken);

        // Setup reactive debouncing pipeline
        var debouncedEvents = _fileEvents
            .Buffer(TimeSpan.FromSeconds(DEBOUNCE_SECONDS), BATCH_SIZE)
            .Where(buffer => buffer.Count > 0);

        // Subscribe to debounced events
        debouncedEvents.Subscribe(async batch =>
        {
            try
            {
                await ProcessEventBatch(batch, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing event batch");
            }
        }, stoppingToken);

        // Start listening to USN Journal
        try
        {
            await _journalReader.StartListeningAsync(_driveLetter, _fileEvents, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in USN Journal reader");
            throw;
        }
    }

    private async Task ProcessEventBatch(IList<UsnJournalEntry> batch, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing batch of {Count} events", batch.Count);

        // Filter for heavy writes or creates
        var significantEvents = batch.Where(e =>
            (e.Reason.HasFlag(UsnReason.DataExtend) && e.FileSize > HEAVY_WRITE_THRESHOLD) ||
            e.Reason.HasFlag(UsnReason.FileCreate) ||
            e.FullPath.Contains("node_modules", StringComparison.OrdinalIgnoreCase) ||
            e.FullPath.Contains("temp", StringComparison.OrdinalIgnoreCase)
        ).ToList();

        if (!significantEvents.Any())
        {
            _logger.LogDebug("No significant events in batch, skipping analysis");
            return;
        }

        _logger.LogInformation("Found {Count} significant events, triggering analysis", significantEvents.Count);

        await _activityPublisher.PublishAsync(new MonitoringActivity
        {
            Type = MonitoringActivityType.DriveSweep,
            Scope = _driveLetter,
            Drive = _driveLetter,
            State = "AnalysisQueued",
            Message = $"{significantEvents.Count} significant events batched",
            Metadata = new Dictionary<string, string>
            {
                ["batchSize"] = batch.Count.ToString(),
                ["significant"] = significantEvents.Count.ToString()
            }
        }, cancellationToken);

        // Send to State Machine Orchestrator for processing
        // This will trigger the IDLE → TRIAGE → PROPOSAL workflow
        var orchestrator = _serviceProvider.GetRequiredService<IStateMachineOrchestrator>();
        await orchestrator.ProcessEventsAsync(significantEvents, cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Drive Monitor stopping");
        await _activityPublisher.PublishAsync(new MonitoringActivity
        {
            Type = MonitoringActivityType.DriveSweep,
            Scope = _driveLetter,
            Drive = _driveLetter,
            State = "Stopped",
            Message = "Drive monitor stopped"
        }, cancellationToken);
        _fileEvents.OnCompleted();
        await base.StopAsync(cancellationToken);
    }
}
