using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using SentinAI.Shared.Models;
using SentinAI.Web.Hubs;

namespace SentinAI.Web.Services;

public interface IUsnJournalMonitorService
{
    bool IsMonitoring { get; }
    Task StartMonitoringAsync(string drivePath, CancellationToken cancellationToken = default);
    Task StopMonitoringAsync();
    IAsyncEnumerable<UsnJournalEntry> GetEventsStreamAsync(CancellationToken cancellationToken = default);
    UsnMonitoringStats GetStats();
    List<UsnJournalEntry> GetRecentEvents(int count = 100);
}

public class UsnMonitoringStats
{
    public bool IsMonitoring { get; set; }
    public string? DrivePath { get; set; }
    public DateTime? StartedAt { get; set; }
    public long TotalEventsProcessed { get; set; }
    public long EventsPerMinute { get; set; }
    public Dictionary<string, int> EventsByType { get; set; } = new();
    public DateTime? LastEventAt { get; set; }
}

/// <summary>
/// Real-time USN Journal monitoring service with SignalR broadcasting.
/// Streams filesystem changes to connected web clients.
/// </summary>
public class UsnJournalMonitorService : IUsnJournalMonitorService, IDisposable
{
    private readonly ILogger<UsnJournalMonitorService> _logger;
    private readonly IHubContext<AgentHub> _hubContext;
    private readonly ConcurrentQueue<UsnJournalEntry> _recentEvents = new();
    private readonly ConcurrentDictionary<string, int> _eventsByType = new();

    private CancellationTokenSource? _monitoringCts;
    private Task? _monitoringTask;
    private string? _currentDrive;
    private DateTime? _startedAt;
    private long _totalEvents;
    private long _eventsLastMinute;
    private DateTime _lastMinuteReset = DateTime.UtcNow;
    private DateTime? _lastEventAt;

    private const int MaxRecentEvents = 1000;

    public bool IsMonitoring => _monitoringTask != null && !_monitoringTask.IsCompleted;

    public UsnJournalMonitorService(
        ILogger<UsnJournalMonitorService> logger,
        IHubContext<AgentHub> hubContext)
    {
        _logger = logger;
        _hubContext = hubContext;
    }

    public async Task StartMonitoringAsync(string drivePath, CancellationToken cancellationToken = default)
    {
        if (IsMonitoring)
        {
            _logger.LogWarning("USN monitoring already running for {Drive}", _currentDrive);
            return;
        }

        _currentDrive = drivePath.TrimEnd('\\');
        if (!_currentDrive.EndsWith(":"))
            _currentDrive += ":";

        _monitoringCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _startedAt = DateTime.UtcNow;
        _totalEvents = 0;

        _logger.LogInformation("🔍 Starting real-time USN Journal monitoring for {Drive}", _currentDrive);

        _monitoringTask = Task.Run(async () =>
        {
            try
            {
                await MonitorUsnJournalAsync(_monitoringCts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("USN monitoring stopped for {Drive}", _currentDrive);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "USN monitoring failed for {Drive}", _currentDrive);
            }
        }, _monitoringCts.Token);

        await Task.CompletedTask;
    }

    public async Task StopMonitoringAsync()
    {
        if (_monitoringCts != null)
        {
            await _monitoringCts.CancelAsync();
            if (_monitoringTask != null)
            {
                try
                {
                    await _monitoringTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("USN monitoring task did not stop in time");
                }
            }
            _monitoringCts.Dispose();
            _monitoringCts = null;
        }

        _currentDrive = null;
        _startedAt = null;
        _logger.LogInformation("🛑 USN Journal monitoring stopped");
    }

    public async IAsyncEnumerable<UsnJournalEntry> GetEventsStreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_recentEvents.TryDequeue(out var entry))
            {
                yield return entry;
            }
            else
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    public UsnMonitoringStats GetStats()
    {
        // Reset events per minute counter
        if ((DateTime.UtcNow - _lastMinuteReset).TotalMinutes >= 1)
        {
            _eventsLastMinute = 0;
            _lastMinuteReset = DateTime.UtcNow;
        }

        return new UsnMonitoringStats
        {
            IsMonitoring = IsMonitoring,
            DrivePath = _currentDrive,
            StartedAt = _startedAt,
            TotalEventsProcessed = _totalEvents,
            EventsPerMinute = _eventsLastMinute,
            EventsByType = new Dictionary<string, int>(_eventsByType),
            LastEventAt = _lastEventAt
        };
    }

    public List<UsnJournalEntry> GetRecentEvents(int count = 100)
    {
        return _recentEvents.TakeLast(count).ToList();
    }

    private async Task MonitorUsnJournalAsync(CancellationToken cancellationToken)
    {
        using var driveHandle = CreateFile(
            $"\\\\.\\{_currentDrive}",
            FileAccess.Read,
            FileShare.ReadWrite,
            IntPtr.Zero,
            FileMode.Open,
            0,
            IntPtr.Zero);

        if (driveHandle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"Failed to open drive handle for {_currentDrive}. Error: {error}. Run as Administrator.");
        }

        // Query USN Journal
        var journalData = QueryUsnJournal(driveHandle);
        long currentUsn = journalData.NextUsn;

        _logger.LogInformation("📖 USN Journal initialized. Starting USN: {Usn}, Journal ID: {JournalId}",
            currentUsn, journalData.UsnJournalID);

        // Broadcast initial status
        await BroadcastStatusAsync(true);

        var batchBuffer = new List<UsnJournalEntry>();
        var lastBroadcast = DateTime.UtcNow;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var records = ReadUsnRecords(driveHandle, ref currentUsn, journalData.UsnJournalID);

                foreach (var record in records)
                {
                    _totalEvents++;
                    _eventsLastMinute++;
                    _lastEventAt = DateTime.UtcNow;

                    // Track by event type
                    var reasonName = GetPrimaryReason(record.Reason);
                    _eventsByType.AddOrUpdate(reasonName, 1, (_, count) => count + 1);

                    // Add to recent events (ring buffer)
                    _recentEvents.Enqueue(record);
                    while (_recentEvents.Count > MaxRecentEvents)
                    {
                        _recentEvents.TryDequeue(out _);
                    }

                    batchBuffer.Add(record);
                }

                // Broadcast batch every 500ms or when buffer has 50+ events
                if (batchBuffer.Count > 0 &&
                    (batchBuffer.Count >= 50 || (DateTime.UtcNow - lastBroadcast).TotalMilliseconds >= 500))
                {
                    await BroadcastEventsAsync(batchBuffer);
                    batchBuffer.Clear();
                    lastBroadcast = DateTime.UtcNow;
                }

                // Small delay to prevent CPU spinning
                await Task.Delay(50, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading USN records");
                await Task.Delay(1000, cancellationToken);
            }
        }

        await BroadcastStatusAsync(false);
    }

    private async Task BroadcastEventsAsync(List<UsnJournalEntry> events)
    {
        try
        {
            // Send to SignalR clients
            await _hubContext.Clients.Group(AgentHub.MonitoringGroupName)
                .SendAsync("UsnEvents", events.Select(e => new
                {
                    e.Usn,
                    e.FileName,
                    e.FullPath,
                    Reason = e.Reason.ToString(),
                    PrimaryReason = GetPrimaryReason(e.Reason),
                    e.FileSize,
                    Timestamp = e.Timestamp.ToString("O"),
                    IsDirectory = (e.Attributes & FileAttributes.Directory) != 0
                }).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to broadcast USN events");
        }
    }

    private async Task BroadcastStatusAsync(bool isRunning)
    {
        try
        {
            await _hubContext.Clients.Group(AgentHub.MonitoringGroupName)
                .SendAsync("UsnMonitoringStatus", new
                {
                    IsMonitoring = isRunning,
                    DrivePath = _currentDrive,
                    StartedAt = _startedAt?.ToString("O"),
                    TotalEvents = _totalEvents
                });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to broadcast monitoring status");
        }
    }

    private static string GetPrimaryReason(UsnReason reason)
    {
        if (reason.HasFlag(UsnReason.FileCreate)) return "Created";
        if (reason.HasFlag(UsnReason.FileDelete)) return "Deleted";
        if (reason.HasFlag(UsnReason.RenameNewName)) return "Renamed";
        if (reason.HasFlag(UsnReason.DataOverwrite) || reason.HasFlag(UsnReason.DataExtend)) return "Modified";
        if (reason.HasFlag(UsnReason.SecurityChange)) return "Security";
        if (reason.HasFlag(UsnReason.Close)) return "Closed";
        return "Other";
    }

    #region P/Invoke

    private USN_JOURNAL_DATA_V0 QueryUsnJournal(SafeFileHandle driveHandle)
    {
        var journalData = new USN_JOURNAL_DATA_V0();
        int bytesReturned = 0;

        bool success = DeviceIoControl(
            driveHandle,
            FSCTL_QUERY_USN_JOURNAL,
            IntPtr.Zero,
            0,
            out journalData,
            Marshal.SizeOf(journalData),
            ref bytesReturned,
            IntPtr.Zero);

        if (!success)
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"Failed to query USN journal. Error: {error}");
        }

        return journalData;
    }

    private List<UsnJournalEntry> ReadUsnRecords(SafeFileHandle driveHandle, ref long startUsn, long journalId)
    {
        var entries = new List<UsnJournalEntry>();

        var readData = new READ_USN_JOURNAL_DATA_V0
        {
            StartUsn = startUsn,
            ReasonMask = 0xFFFFFFFF,
            ReturnOnlyOnClose = 0,
            Timeout = 0,
            BytesToWaitFor = 0,
            UsnJournalID = journalId
        };

        const int bufferSize = 64 * 1024;
        byte[] buffer = new byte[bufferSize];
        int bytesReturned = 0;

        bool success = DeviceIoControl(
            driveHandle,
            FSCTL_READ_USN_JOURNAL,
            ref readData,
            Marshal.SizeOf(readData),
            buffer,
            bufferSize,
            ref bytesReturned,
            IntPtr.Zero);

        if (!success || bytesReturned <= 8)
        {
            return entries;
        }

        // First 8 bytes contain the next USN
        startUsn = BitConverter.ToInt64(buffer, 0);
        int offset = 8;

        while (offset < bytesReturned)
        {
            try
            {
                int recordLength = BitConverter.ToInt32(buffer, offset);
                if (recordLength < 60 || offset + recordLength > bytesReturned)
                    break;

                var entry = ParseUsnRecord(buffer, offset);
                if (entry != null)
                {
                    entries.Add(entry);
                }

                offset += recordLength;
            }
            catch
            {
                break;
            }
        }

        return entries;
    }

    private static UsnJournalEntry? ParseUsnRecord(byte[] buffer, int offset)
    {
        int recordLength = BitConverter.ToInt32(buffer, offset);
        if (recordLength < 60) return null;

        long usn = BitConverter.ToInt64(buffer, offset + 8);
        long timestamp = BitConverter.ToInt64(buffer, offset + 16);
        uint reason = BitConverter.ToUInt32(buffer, offset + 24);
        uint attributes = BitConverter.ToUInt32(buffer, offset + 52);
        short fileNameLength = BitConverter.ToInt16(buffer, offset + 56);
        short fileNameOffset = BitConverter.ToInt16(buffer, offset + 58);

        if (fileNameLength <= 0 || offset + fileNameOffset + fileNameLength > buffer.Length)
            return null;

        string fileName = System.Text.Encoding.Unicode.GetString(
            buffer, offset + fileNameOffset, fileNameLength);

        return new UsnJournalEntry
        {
            Usn = usn,
            FileName = fileName,
            FullPath = fileName,
            Reason = (UsnReason)reason,
            FileSize = 0,
            Timestamp = DateTime.FromFileTime(timestamp),
            Attributes = (FileAttributes)attributes
        };
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        FileAccess dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        out USN_JOURNAL_DATA_V0 lpOutBuffer,
        int nOutBufferSize,
        ref int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref READ_USN_JOURNAL_DATA_V0 lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        ref int lpBytesReturned,
        IntPtr lpOverlapped);

    private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900f4;
    private const uint FSCTL_READ_USN_JOURNAL = 0x000900bb;

    [StructLayout(LayoutKind.Sequential)]
    private struct USN_JOURNAL_DATA_V0
    {
        public long UsnJournalID;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public long MaximumSize;
        public long AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct READ_USN_JOURNAL_DATA_V0
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public long UsnJournalID;
    }

    #endregion

    public void Dispose()
    {
        _monitoringCts?.Cancel();
        _monitoringCts?.Dispose();
    }
}
