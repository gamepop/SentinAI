using System;
using System.IO;

namespace SentinAI.Shared.Models;

/// <summary>
/// Represents a USN Journal entry from the NTFS change journal.
/// Provides information about filesystem changes in real-time.
/// </summary>
public record UsnJournalEntry
{
    /// <summary>
    /// The Update Sequence Number - unique identifier for this change
    /// </summary>
    public long Usn { get; init; }

    /// <summary>
    /// The name of the file (without path)
    /// </summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>
    /// The full path to the file
    /// </summary>
    public string FullPath { get; init; } = string.Empty;

    /// <summary>
    /// The reason for this USN record (file created, modified, deleted, etc.)
    /// </summary>
    public UsnReason Reason { get; init; }

    /// <summary>
    /// File size in bytes
    /// </summary>
    public long FileSize { get; init; }

    /// <summary>
    /// Timestamp when this change occurred
    /// </summary>
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// File attributes (hidden, system, readonly, etc.)
    /// </summary>
    public FileAttributes Attributes { get; init; }
}

/// <summary>
/// Flags indicating why a USN record was created.
/// Maps to USN_REASON_* constants from Win32 API.
/// </summary>
[Flags]
public enum UsnReason : uint
{
    None = 0,
    DataOverwrite = 0x00000001,
    DataExtend = 0x00000002,
    DataTruncation = 0x00000004,
    NamedDataOverwrite = 0x00000010,
    NamedDataExtend = 0x00000020,
    NamedDataTruncation = 0x00000040,
    FileCreate = 0x00000100,
    FileDelete = 0x00000200,
    PropertyChange = 0x00000400,
    SecurityChange = 0x00000800,
    RenameOldName = 0x00001000,
    RenameNewName = 0x00002000,
    IndexableChange = 0x00004000,
    BasicInfoChange = 0x00008000,
    HardLinkChange = 0x00010000,
    CompressionChange = 0x00020000,
    EncryptionChange = 0x00040000,
    ObjectIdChange = 0x00080000,
    ReparsePointChange = 0x00100000,
    StreamChange = 0x00200000,
    Close = 0x80000000
}
