using System.Runtime.InteropServices;
using System.Reactive.Subjects;
using SentinAI.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace SentinAI.SentinelService.Services;

public interface IUsnJournalReader
{
    Task StartListeningAsync(string drivePath, ISubject<UsnJournalEntry> eventStream, CancellationToken cancellationToken);
}

/// <summary>
/// Low-level USN Journal reader using P/Invoke
/// More efficient than FileSystemWatcher for monitoring entire drive
/// </summary>
public class UsnJournalReader : IUsnJournalReader
{
    private readonly ILogger<UsnJournalReader> _logger;

    public UsnJournalReader(ILogger<UsnJournalReader> logger)
    {
        _logger = logger;
    }

    public async Task StartListeningAsync(
        string drivePath,
        ISubject<UsnJournalEntry> eventStream,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting USN Journal reader for {Drive}", drivePath);

        await Task.Run(() =>
        {
            using var driveHandle = CreateFile(
                $"\\\\.\\{drivePath.TrimEnd('\\')}",
                FileAccess.Read,
                FileShare.ReadWrite,
                IntPtr.Zero,
                FileMode.Open,
                0,
                IntPtr.Zero);

            if (driveHandle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"Failed to open drive handle. Error: {error}");
            }

            // Query USN Journal data
            var journalData = QueryUsnJournal(driveHandle);
            long currentUsn = journalData.NextUsn;

            _logger.LogInformation("USN Journal initialized. Starting USN: {Usn}", currentUsn);

            // Read USN records in a loop
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var records = ReadUsnRecords(driveHandle, currentUsn);

                    foreach (var record in records)
                    {
                        currentUsn = Math.Max(currentUsn, record.Usn);
                        eventStream.OnNext(record);
                    }

                    // Sleep briefly to avoid CPU spinning
                    Thread.Sleep(100);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error reading USN records");
                    Thread.Sleep(1000); // Back off on error
                }
            }
        }, cancellationToken);
    }

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
            throw new InvalidOperationException("Failed to query USN journal");
        }

        return journalData;
    }

    private List<UsnJournalEntry> ReadUsnRecords(SafeFileHandle driveHandle, long startUsn)
    {
        var entries = new List<UsnJournalEntry>();

        // Setup read parameters
        var readData = new READ_USN_JOURNAL_DATA_V0
        {
            StartUsn = startUsn,
            ReasonMask = 0xFFFFFFFF, // All reasons
            ReturnOnlyOnClose = 0,
            Timeout = 0,
            BytesToWaitFor = 0,
            UsnJournalID = 0
        };

        const int bufferSize = 64 * 1024; // 64KB buffer
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

        if (!success || bytesReturned == 0)
        {
            return entries; // No new records
        }

        // Parse USN records from buffer
        int offset = 8; // Skip first 8 bytes (next USN)

        while (offset < bytesReturned)
        {
            try
            {
                var record = ParseUsnRecord(buffer, offset);
                if (record != null)
                {
                    entries.Add(record);
                }

                // Move to next record
                int recordLength = BitConverter.ToInt32(buffer, offset);
                if (recordLength == 0) break;
                offset += recordLength;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error parsing USN record at offset {Offset}", offset);
                break;
            }
        }

        return entries;
    }

    private UsnJournalEntry? ParseUsnRecord(byte[] buffer, int offset)
    {
        // USN_RECORD_V2 structure parsing
        int recordLength = BitConverter.ToInt32(buffer, offset);
        if (recordLength < 60) return null; // Minimum record size

        long usn = BitConverter.ToInt64(buffer, offset + 8);
        long timestamp = BitConverter.ToInt64(buffer, offset + 16);
        uint reason = BitConverter.ToUInt32(buffer, offset + 24);
        uint attributes = BitConverter.ToUInt32(buffer, offset + 52);
        short fileNameLength = BitConverter.ToInt16(buffer, offset + 56);
        short fileNameOffset = BitConverter.ToInt16(buffer, offset + 58);

        if (fileNameLength == 0) return null;

        string fileName = System.Text.Encoding.Unicode.GetString(
            buffer,
            offset + fileNameOffset,
            fileNameLength);

        return new UsnJournalEntry
        {
            Usn = usn,
            FileName = fileName,
            FullPath = fileName, // TODO: Resolve full path using FileReferenceNumber
            Reason = (UsnReason)reason,
            FileSize = 0, // Would need additional query
            Timestamp = DateTime.FromFileTime(timestamp),
            Attributes = (FileAttributes)attributes
        };
    }

    #region P/Invoke Declarations

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
}
