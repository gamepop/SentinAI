# SentinAI Build Script
# Builds all projects and creates MSIX package

param(
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$CreatePackage
)

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "    SentinAI Build Script" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

# Check prerequisites
Write-Host "Checking prerequisites..." -ForegroundColor Yellow

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "ERROR: .NET SDK not found. Please install .NET 9 SDK (or .NET 8 as fallback)." -ForegroundColor Red
    Write-Host "" -ForegroundColor Red
    Write-Host "Download .NET 9 SDK from:" -ForegroundColor Yellow
    Write-Host "  https://dotnet.microsoft.com/download/dotnet/9.0" -ForegroundColor Cyan
    Write-Host "" -ForegroundColor Yellow
    Write-Host "Or install via winget:" -ForegroundColor Yellow
    Write-Host "  winget install Microsoft.DotNet.SDK.9" -ForegroundColor Cyan
    exit 1
}

$dotnetVersion = dotnet --version
$requiredMajorVersion = 9

# Parse major version
$majorVersion = [int]($dotnetVersion.Split('.')[0])

if ($majorVersion -lt 8) {
    Write-Host "ERROR: .NET SDK version $dotnetVersion is too old." -ForegroundColor Red
    Write-Host "Please install .NET 9 SDK (or .NET 8 minimum)." -ForegroundColor Red
    exit 1
}

if ($majorVersion -eq 8) {
    Write-Host "WARNING: .NET SDK version: $dotnetVersion (Recommended: .NET 9+)" -ForegroundColor Yellow
} elseif ($majorVersion -eq 9) {
    Write-Host "SUCCESS: .NET SDK version: $dotnetVersion" -ForegroundColor Green
} elseif ($majorVersion -ge 10) {
    Write-Host "SUCCESS: .NET SDK version: $dotnetVersion (.NET 10 Preview/RC detected!)" -ForegroundColor Cyan
    Write-Host "  Note: MSIX packaging may not work until Microsoft updates the tools" -ForegroundColor Yellow
} else {
    Write-Host "SUCCESS: .NET SDK version: $dotnetVersion" -ForegroundColor Green
}

# Check for Windows App SDK
$windowsAppSDK = Get-Command "makeappx.exe" -ErrorAction SilentlyContinue
if (-not $windowsAppSDK) {
    Write-Host "WARNING: Windows App SDK not found (required for MSIX packaging)" -ForegroundColor Yellow
    Write-Host "  Install via: winget install Microsoft.WindowsAppRuntime.1.6" -ForegroundColor Cyan
}

# Clean previous builds
Write-Host "`nCleaning previous builds..." -ForegroundColor Yellow
dotnet clean SentinAI.sln -c $Configuration --nologo

# Restore NuGet packages
Write-Host "`nRestoring NuGet packages..." -ForegroundColor Yellow
dotnet restore SentinAI.sln --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: NuGet restore failed" -ForegroundColor Red
    exit 1
}
Write-Host "SUCCESS: NuGet packages restored" -ForegroundColor Green

# Build Shared library
Write-Host "`nBuilding SentinAI.Shared..." -ForegroundColor Yellow
dotnet build src/SentinAI.Shared/SentinAI.Shared.csproj -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: SentinAI.Shared build failed" -ForegroundColor Red
    exit 1
}
Write-Host "SUCCESS: SentinAI.Shared built successfully" -ForegroundColor Green

# Build Sentinel Service
Write-Host "`nBuilding SentinAI.SentinelService..." -ForegroundColor Yellow
dotnet build src/SentinAI.SentinelService/SentinAI.SentinelService.csproj -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: SentinAI.SentinelService build failed" -ForegroundColor Red
    exit 1
}
Write-Host "SUCCESS: SentinAI.SentinelService built successfully" -ForegroundColor Green

# Build Web UI
Write-Host "`nBuilding SentinAI.Web..." -ForegroundColor Yellow
dotnet build src/SentinAI.Web/SentinAI.Web.csproj -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: SentinAI.Web build failed" -ForegroundColor Red
    exit 1
}
Write-Host "SUCCESS: SentinAI.Web built successfully" -ForegroundColor Green

# Run tests (if not skipped)
if (-not $SkipTests) {
    Write-Host "`nRunning tests..." -ForegroundColor Yellow
    # dotnet test SentinAI.sln -c $Configuration --nologo --no-build
    Write-Host "WARNING: Tests not yet implemented" -ForegroundColor Yellow
}

# Create MSIX package (if requested)
if ($CreatePackage) {
    Write-Host "`nCreating MSIX package..." -ForegroundColor Yellow
    Write-Host "WARNING: MSIX packaging requires Visual Studio with Windows App SDK" -ForegroundColor Yellow
    Write-Host "  Please use Visual Studio to create the package:" -ForegroundColor Yellow
    Write-Host "  1. Open SentinAI.sln in Visual Studio" -ForegroundColor Cyan
    Write-Host "  2. Right-click SentinAI.Packaging" -ForegroundColor Cyan
    Write-Host "  3. Select 'Publish' -> 'Create App Packages'" -ForegroundColor Cyan
}

Write-Host "`n============================================" -ForegroundColor Cyan
Write-Host "    Build completed successfully!" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Output locations:" -ForegroundColor Yellow
Write-Host "  Sentinel Service: src/SentinAI.SentinelService/bin/$Configuration/net9.0-windows10.0.22621.0/" -ForegroundColor Cyan
Write-Host "  Web UI:           src/SentinAI.Web/bin/$Configuration/net9.0/" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Start the Sentinel Service: dotnet run --project src\SentinAI.SentinelService" -ForegroundColor Cyan
Write-Host "  2. Start the Web UI: dotnet run --project src\SentinAI.Web" -ForegroundColor Cyan
Write-Host "  3. Open browser to: https://localhost:5001" -ForegroundColor Cyan
Write-Host ""
