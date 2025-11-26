using Microsoft.Extensions.Logging;
using Moq;
using SentinAI.Web.Services;

namespace SentinAI.Web.Tests.Services;

public class DuplicateFileServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly Mock<ILogger<DuplicateFileService>> _loggerMock;
    private readonly DuplicateFileService _service;

    public DuplicateFileServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"duplicate_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);

        _loggerMock = new Mock<ILogger<DuplicateFileService>>();
        _service = new DuplicateFileService(_loggerMock.Object);
    }

    public void Dispose()
    {
        _service.ClearCache();
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private async Task CreateTestFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_testDirectory, relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
        await File.WriteAllTextAsync(fullPath, content);
    }

    [Fact]
    public async Task ScanForDuplicatesAsync_EmptyDirectory_ReturnsEmptyResults()
    {
        // Arrange
        var options = new DuplicateScanOptions();

        // Act
        var result = await _service.ScanForDuplicatesAsync(_testDirectory, options);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result.DuplicateGroups);
        Assert.Equal(0, result.TotalDuplicateFiles);
        Assert.Equal(0, result.TotalWastedBytes);
    }

    [Fact]
    public async Task ScanForDuplicatesAsync_NoDuplicates_ReturnsEmptyGroups()
    {
        // Arrange
        await CreateTestFile("file1.txt", "Content One - unique");
        await CreateTestFile("file2.txt", "Content Two - also unique");
        await CreateTestFile("file3.txt", "Content Three - different");

        var options = new DuplicateScanOptions { MinFileSizeBytes = 1 };

        // Act
        var result = await _service.ScanForDuplicatesAsync(_testDirectory, options);

        // Assert
        Assert.Empty(result.DuplicateGroups);
    }

    [Fact]
    public async Task ScanForDuplicatesAsync_WithDuplicates_FindsThem()
    {
        // Arrange
        const string duplicateContent = "This is duplicate content for testing purposes.";
        await CreateTestFile("original.txt", duplicateContent);
        await CreateTestFile("copy1.txt", duplicateContent);
        await CreateTestFile("copy2.txt", duplicateContent);
        await CreateTestFile("unique.txt", "This is unique content.");

        var options = new DuplicateScanOptions { MinFileSizeBytes = 1 };

        // Act
        var result = await _service.ScanForDuplicatesAsync(_testDirectory, options);

        // Assert
        Assert.Single(result.DuplicateGroups);
        Assert.Equal(3, result.DuplicateGroups[0].Files.Count);
        Assert.Equal(2, result.TotalDuplicateFiles); // 3 files - 1 original = 2 duplicates
    }

    [Fact]
    public async Task ScanForDuplicatesAsync_MultipleDuplicateGroups()
    {
        // Arrange
        const string content1 = "Content group one - duplicate A";
        const string content2 = "Content group two - duplicate B";

        await CreateTestFile("group1/file1.txt", content1);
        await CreateTestFile("group1/file2.txt", content1);
        await CreateTestFile("group2/file1.txt", content2);
        await CreateTestFile("group2/file2.txt", content2);

        var options = new DuplicateScanOptions { MinFileSizeBytes = 1 };

        // Act
        var result = await _service.ScanForDuplicatesAsync(_testDirectory, options);

        // Assert
        Assert.Equal(2, result.DuplicateGroups.Count);
    }

    [Fact]
    public async Task ScanForDuplicatesAsync_RespectsMinFileSize()
    {
        // Arrange
        const string smallContent = "Hi"; // 2 bytes
        const string largeContent = "This is a much larger content for testing minimum file size."; // > 50 bytes

        await CreateTestFile("small1.txt", smallContent);
        await CreateTestFile("small2.txt", smallContent);
        await CreateTestFile("large1.txt", largeContent);
        await CreateTestFile("large2.txt", largeContent);

        var options = new DuplicateScanOptions { MinFileSizeBytes = 50 };

        // Act
        var result = await _service.ScanForDuplicatesAsync(_testDirectory, options);

        // Assert
        Assert.Single(result.DuplicateGroups);
        Assert.Contains("large", result.DuplicateGroups[0].Files[0].FileName);
    }

    [Fact]
    public async Task ScanForDuplicatesAsync_ExcludesExtensions()
    {
        // Arrange
        const string content = "Same content in different file types";
        await CreateTestFile("file.txt", content);
        await CreateTestFile("file.log", content);
        await CreateTestFile("file.dll", content); // Should be excluded

        var options = new DuplicateScanOptions
        {
            MinFileSizeBytes = 1,
            ExcludedExtensions = new[] { ".dll" }
        };

        // Act
        var result = await _service.ScanForDuplicatesAsync(_testDirectory, options);

        // Assert
        Assert.Single(result.DuplicateGroups);
        Assert.Equal(2, result.DuplicateGroups[0].Files.Count);
        Assert.All(result.DuplicateGroups[0].Files, f => Assert.DoesNotContain(".dll", f.FileName));
    }

    [Fact]
    public async Task ScanForDuplicatesAsync_TracksScanDuration()
    {
        // Arrange
        await CreateTestFile("test.txt", "Test content");
        var options = new DuplicateScanOptions { MinFileSizeBytes = 1 };

        // Act
        var result = await _service.ScanForDuplicatesAsync(_testDirectory, options);

        // Assert
        Assert.True(result.ScanDuration > TimeSpan.Zero);
    }

    [Fact]
    public async Task ScanForDuplicatesAsync_SetsRootPath()
    {
        // Arrange
        var options = new DuplicateScanOptions();

        // Act
        var result = await _service.ScanForDuplicatesAsync(_testDirectory, options);

        // Assert
        Assert.Equal(_testDirectory, result.RootPath);
    }

    [Fact]
    public async Task GetDuplicateGroupsAsync_AfterScan_ReturnsResults()
    {
        // Arrange
        const string content = "Duplicate content";
        await CreateTestFile("file1.txt", content);
        await CreateTestFile("file2.txt", content);

        await _service.ScanForDuplicatesAsync(_testDirectory, new DuplicateScanOptions { MinFileSizeBytes = 1 });

        // Act
        var groups = await _service.GetDuplicateGroupsAsync();

        // Assert
        Assert.Single(groups);
    }

    [Fact]
    public async Task GetDuplicateGroupsAsync_BeforeScan_ReturnsEmpty()
    {
        // Act
        var groups = await _service.GetDuplicateGroupsAsync();

        // Assert
        Assert.Empty(groups);
    }

    [Fact]
    public async Task GetLastScanRootPathAsync_AfterScan_ReturnsPath()
    {
        // Arrange
        await _service.ScanForDuplicatesAsync(_testDirectory, new DuplicateScanOptions());

        // Act
        var rootPath = await _service.GetLastScanRootPathAsync();

        // Assert
        Assert.Equal(_testDirectory, rootPath);
    }

    [Fact]
    public async Task GetLastScanRootPathAsync_BeforeScan_ReturnsNull()
    {
        // Act
        var rootPath = await _service.GetLastScanRootPathAsync();

        // Assert
        Assert.Null(rootPath);
    }

    [Fact]
    public async Task DeleteFilesAsync_DeletesSpecifiedFiles()
    {
        // Arrange
        await CreateTestFile("to_delete.txt", "Delete me");
        var filePath = Path.Combine(_testDirectory, "to_delete.txt");

        // Act
        var result = await _service.DeleteFilesAsync(new[] { filePath });

        // Assert
        Assert.Equal(1, result.DeletedCount);
        Assert.True(result.BytesFreed > 0);
        Assert.Empty(result.Errors);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task DeleteFilesAsync_NonexistentFile_AddsError()
    {
        // Arrange
        var nonexistentPath = Path.Combine(_testDirectory, "nonexistent.txt");

        // Act
        var result = await _service.DeleteFilesAsync(new[] { nonexistentPath });

        // Assert
        Assert.Equal(0, result.DeletedCount);
        Assert.Single(result.Errors);
        Assert.Contains("not found", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteFilesAsync_IgnoresEmptyPaths()
    {
        // Arrange
        var paths = new[] { "", "   ", null! };

        // Act
        var result = await _service.DeleteFilesAsync(paths);

        // Assert
        Assert.Equal(0, result.DeletedCount);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task DeleteFilesAsync_DeduplicatesPaths()
    {
        // Arrange
        await CreateTestFile("file.txt", "Test content");
        var filePath = Path.Combine(_testDirectory, "file.txt");

        // Act
        var result = await _service.DeleteFilesAsync(new[] { filePath, filePath, filePath });

        // Assert
        Assert.Equal(1, result.DeletedCount); // Only deleted once
    }

    [Fact]
    public void ClearCache_ClearsAllData()
    {
        // Act
        _service.ClearCache();

        // Assert - should be able to get empty results
        var groups = _service.GetDuplicateGroupsAsync().Result;
        var rootPath = _service.GetLastScanRootPathAsync().Result;

        Assert.Empty(groups);
        Assert.Null(rootPath);
    }

    [Fact]
    public async Task ScanForDuplicatesAsync_CancellationToken_StopsScan()
    {
        // Arrange
        await CreateTestFile("file1.txt", "Content");
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _service.ScanForDuplicatesAsync(_testDirectory, new DuplicateScanOptions(), cancellationToken: cts.Token));
    }

    [Fact]
    public async Task ScanForDuplicatesAsync_ReportsProgress()
    {
        // Arrange
        await CreateTestFile("file1.txt", "Content 1");
        await CreateTestFile("file2.txt", "Content 2");

        var progressReports = new List<DuplicateScanProgress>();
        var progress = new Progress<DuplicateScanProgress>(p => progressReports.Add(p));

        var options = new DuplicateScanOptions { MinFileSizeBytes = 1 };

        // Act
        await _service.ScanForDuplicatesAsync(_testDirectory, options, progress);

        // Assert
        Assert.NotEmpty(progressReports);
    }

    [Fact]
    public async Task DuplicateGroup_WastedBytes_CalculatesCorrectly()
    {
        // Arrange
        const string content = "Some content that will be duplicated";
        await CreateTestFile("original.txt", content);
        await CreateTestFile("copy1.txt", content);
        await CreateTestFile("copy2.txt", content);

        var options = new DuplicateScanOptions { MinFileSizeBytes = 1 };

        // Act
        var result = await _service.ScanForDuplicatesAsync(_testDirectory, options);

        // Assert
        var group = result.DuplicateGroups[0];
        var expectedWasted = group.FileSize * (group.Files.Count - 1);
        Assert.Equal(expectedWasted, group.WastedBytes);
    }

    [Fact]
    public async Task DuplicateGroup_OldestFileIsOriginal()
    {
        // Arrange
        const string content = "Same content";
        var file1 = Path.Combine(_testDirectory, "file1.txt");
        var file2 = Path.Combine(_testDirectory, "file2.txt");

        await File.WriteAllTextAsync(file1, content);
        await Task.Delay(100); // Ensure different creation times
        await File.WriteAllTextAsync(file2, content);

        var options = new DuplicateScanOptions { MinFileSizeBytes = 1 };

        // Act
        var result = await _service.ScanForDuplicatesAsync(_testDirectory, options);

        // Assert
        var group = result.DuplicateGroups[0];
        var originalFile = group.Files.First(f => f.IsOriginal);
        Assert.NotNull(originalFile);
        Assert.Equal(group.Files.Min(f => f.Created), originalFile.Created);
    }
}

public class DuplicateScanOptionsTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var options = new DuplicateScanOptions();

        // Assert
        Assert.Equal(1024, options.MinFileSizeBytes);
        Assert.Equal(10L * 1024 * 1024 * 1024, options.MaxFileSizeBytes);
        Assert.Contains(".dll", options.ExcludedExtensions);
        Assert.Contains(".exe", options.ExcludedExtensions);
        Assert.Contains("Windows", options.ExcludedFolders);
        Assert.False(options.IncludeHiddenFiles);
        Assert.Equal(Environment.ProcessorCount, options.MaxDegreeOfParallelism);
        Assert.True(options.IncludeSubdirectories);
    }
}

public class DuplicateFileModelTests
{
    [Fact]
    public void DuplicateFile_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var file = new DuplicateFile();

        // Assert
        Assert.Equal(string.Empty, file.FilePath);
        Assert.Equal(string.Empty, file.FileName);
        Assert.Equal(0, file.FileSize);
        Assert.False(file.IsOriginal);
        Assert.False(file.MarkedForDeletion);
    }

    [Fact]
    public void DuplicateGroup_DuplicateCount_CalculatesCorrectly()
    {
        // Arrange
        var group = new DuplicateGroup
        {
            Files = new List<DuplicateFile>
            {
                new() { FileName = "original.txt" },
                new() { FileName = "copy1.txt" },
                new() { FileName = "copy2.txt" }
            }
        };

        // Assert
        Assert.Equal(2, group.DuplicateCount);
    }
}

public class DeleteDuplicatesResultTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var result = new DeleteDuplicatesResult();

        // Assert
        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(0, result.BytesFreed);
        Assert.NotNull(result.Errors);
        Assert.Empty(result.Errors);
    }
}
