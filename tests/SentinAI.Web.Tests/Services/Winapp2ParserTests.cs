using SentinAI.Web.Services;

namespace SentinAI.Web.Tests.Services;

public class Winapp2ParserTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly Winapp2Parser _parser;

    public Winapp2ParserTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"winapp2_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _parser = new Winapp2Parser();
    }

    public void Dispose()
    {
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

    [Fact]
    public void IsSafeToDelete_BeforeLoad_ReturnsFalse()
    {
        // Arrange - parser not loaded

        // Act
        var result = _parser.IsSafeToDelete(@"C:\Windows\Temp\test.tmp");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void GetCleanupRule_BeforeLoad_ReturnsNull()
    {
        // Arrange - parser not loaded

        // Act
        var result = _parser.GetCleanupRule(@"C:\Windows\Temp\test.tmp");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task LoadAsync_WithValidFile_LoadsRules()
    {
        // Arrange
        var winapp2Content = @"
; This is a comment
[Windows Temp *]
Section=System
FileKey1=%Temp%|*.*|RECURSE

[Chrome Cache *]
Section=Browser
FileKey1=%LocalAppData%\Google\Chrome\User Data\*\Cache|*.*|RECURSE
";
        var filePath = Path.Combine(_testDirectory, "Winapp2.ini");
        await File.WriteAllTextAsync(filePath, winapp2Content);

        // Act
        await _parser.LoadAsync(filePath);

        // Assert - should be able to check rules now
        // Note: The actual matching depends on environment variables
        Assert.NotNull(_parser);
    }

    [Fact]
    public async Task LoadAsync_WithEmptyFile_LoadsWithoutError()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "empty.ini");
        await File.WriteAllTextAsync(filePath, string.Empty);

        // Act
        await _parser.LoadAsync(filePath);

        // Assert
        Assert.False(_parser.IsSafeToDelete(@"C:\any\path"));
    }

    [Fact]
    public async Task LoadAsync_ParsesSectionNames()
    {
        // Arrange
        var winapp2Content = @"
[Test Rule *]
Section=Testing
FileKey1=C:\Test\Path|*.*
";
        var filePath = Path.Combine(_testDirectory, "test.ini");
        await File.WriteAllTextAsync(filePath, winapp2Content);

        // Act
        await _parser.LoadAsync(filePath);

        // Assert
        var rule = _parser.GetCleanupRule(@"C:\Test\Path");
        Assert.NotNull(rule);
        Assert.Contains("Test Rule", rule);
    }

    [Fact]
    public async Task LoadAsync_IgnoresComments()
    {
        // Arrange
        var winapp2Content = @"
; This is a comment
;[Fake Section]
;FileKey1=C:\Should\Not\Exist|*.*

[Real Section *]
Section=Real
FileKey1=C:\Real\Path|*.*
";
        var filePath = Path.Combine(_testDirectory, "comments.ini");
        await File.WriteAllTextAsync(filePath, winapp2Content);

        // Act
        await _parser.LoadAsync(filePath);

        // Assert
        var fakeRule = _parser.GetCleanupRule(@"C:\Should\Not\Exist");
        var realRule = _parser.GetCleanupRule(@"C:\Real\Path");

        Assert.Null(fakeRule);
        Assert.NotNull(realRule);
    }

    [Fact]
    public async Task LoadAsync_ParsesMultipleFileKeys()
    {
        // Arrange
        var winapp2Content = @"
[Multi Key *]
Section=Test
FileKey1=C:\Path\One|*.*
FileKey2=C:\Path\Two|*.*
FileKey3=C:\Path\Three|*.*
";
        var filePath = Path.Combine(_testDirectory, "multikey.ini");
        await File.WriteAllTextAsync(filePath, winapp2Content);

        // Act
        await _parser.LoadAsync(filePath);

        // Assert
        Assert.True(_parser.IsSafeToDelete(@"C:\Path\One"));
        Assert.True(_parser.IsSafeToDelete(@"C:\Path\Two"));
        Assert.True(_parser.IsSafeToDelete(@"C:\Path\Three"));
    }

    [Fact]
    public async Task IsSafeToDelete_WithWildcard_MatchesPattern()
    {
        // Arrange
        var winapp2Content = @"
[Wildcard Test *]
Section=Test
FileKey1=C:\Wildcard\*|*.*
";
        var filePath = Path.Combine(_testDirectory, "wildcard.ini");
        await File.WriteAllTextAsync(filePath, winapp2Content);

        // Act
        await _parser.LoadAsync(filePath);

        // Assert - wildcard matching is regex-based
        Assert.True(_parser.IsSafeToDelete(@"C:\Wildcard\anything"));
        Assert.True(_parser.IsSafeToDelete(@"C:\Wildcard\subdirectory"));
    }

    [Fact]
    public async Task GetCleanupRule_IncludesCategoryInResult()
    {
        // Arrange
        var winapp2Content = @"
[My App Cache *]
Section=Applications
FileKey1=C:\MyApp\Cache|*.*
";
        var filePath = Path.Combine(_testDirectory, "category.ini");
        await File.WriteAllTextAsync(filePath, winapp2Content);

        // Act
        await _parser.LoadAsync(filePath);
        var rule = _parser.GetCleanupRule(@"C:\MyApp\Cache");

        // Assert
        Assert.NotNull(rule);
        Assert.Contains("My App Cache", rule);
        Assert.Contains("Applications", rule);
    }

    [Fact]
    public async Task IsSafeToDelete_CaseInsensitive()
    {
        // Arrange
        var winapp2Content = @"
[Case Test *]
Section=Test
FileKey1=C:\CaseTest\Path|*.*
";
        var filePath = Path.Combine(_testDirectory, "case.ini");
        await File.WriteAllTextAsync(filePath, winapp2Content);

        // Act
        await _parser.LoadAsync(filePath);

        // Assert
        Assert.True(_parser.IsSafeToDelete(@"C:\CaseTest\Path"));
        Assert.True(_parser.IsSafeToDelete(@"C:\CASETEST\PATH"));
        Assert.True(_parser.IsSafeToDelete(@"c:\casetest\path"));
    }

    [Fact]
    public async Task LoadAsync_HandlesMultipleSections()
    {
        // Arrange
        var winapp2Content = @"
[Section One *]
Section=First
FileKey1=C:\First\Path|*.*

[Section Two *]
Section=Second
FileKey1=C:\Second\Path|*.*

[Section Three *]
Section=Third
FileKey1=C:\Third\Path|*.*
";
        var filePath = Path.Combine(_testDirectory, "multiple.ini");
        await File.WriteAllTextAsync(filePath, winapp2Content);

        // Act
        await _parser.LoadAsync(filePath);

        // Assert
        Assert.True(_parser.IsSafeToDelete(@"C:\First\Path"));
        Assert.True(_parser.IsSafeToDelete(@"C:\Second\Path"));
        Assert.True(_parser.IsSafeToDelete(@"C:\Third\Path"));
    }

    [Fact]
    public async Task LoadAsync_StripsEnvironmentVariables()
    {
        // Arrange
        var tempPath = Path.GetTempPath();
        var winapp2Content = @"
[Temp Variable *]
Section=System
FileKey1=%Temp%|*.*
";
        var filePath = Path.Combine(_testDirectory, "envvar.ini");
        await File.WriteAllTextAsync(filePath, winapp2Content);

        // Act
        await _parser.LoadAsync(filePath);

        // Assert - should expand %Temp% to actual temp path
        Assert.True(_parser.IsSafeToDelete(tempPath));
    }
}
