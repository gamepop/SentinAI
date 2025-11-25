<#
.SYNOPSIS
    Downloads Phi-3 Mini ONNX models for SentinAI Brain

.DESCRIPTION
    This script downloads both CPU and DirectML (GPU) variants of the 
    Phi-3 Mini 4K Instruct ONNX model from HuggingFace.

.PARAMETER Provider
    Which model to download: "CPU", "DirectML", or "Both" (default)

.PARAMETER Force
    Force re-download even if models already exist

.EXAMPLE
    .\download-models.ps1
    Downloads both CPU and DirectML models

.EXAMPLE
    .\download-models.ps1 -Provider CPU
    Downloads only the CPU model

.EXAMPLE
    .\download-models.ps1 -Provider DirectML -Force
    Force re-downloads the DirectML model
#>

param(
    [ValidateSet("CPU", "DirectML", "Both")]
    [string]$Provider = "Both",
    
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# Configuration
$HuggingFaceRepo = "microsoft/Phi-3-mini-4k-instruct-onnx"
$BaseUrl = "https://huggingface.co/$HuggingFaceRepo/resolve/main"

$ModelsDir = Join-Path $env:LOCALAPPDATA "SentinAI\Models"

# Model configurations
$Models = @{
    "CPU" = @{
        Subdirectory = "cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4"
        LocalFolder = "Phi3-Mini-CPU"
        ModelFile = "phi3-mini-4k-instruct-cpu-int4-rtn-block-32-acc-level-4.onnx"
        DataFile = "phi3-mini-4k-instruct-cpu-int4-rtn-block-32-acc-level-4.onnx.data"
    }
    "DirectML" = @{
        Subdirectory = "directml/directml-int4-awq-block-128"
        LocalFolder = "Phi3-Mini-DirectML"
        ModelFile = "model.onnx"
        DataFile = "model.onnx.data"
    }
}

$ConfigFiles = @(
    "genai_config.json",
    "config.json", 
    "tokenizer.json",
    "tokenizer_config.json",
    "special_tokens_map.json",
    "tokenizer.model",
    "added_tokens.json"
)

function Write-Header {
    param([string]$Text)
    Write-Host ""
    Write-Host "================================================================" -ForegroundColor Cyan
    Write-Host "  $Text" -ForegroundColor Cyan
    Write-Host "================================================================" -ForegroundColor Cyan
    Write-Host ""
}

function Write-Step {
    param([string]$Text)
    Write-Host "  -> $Text" -ForegroundColor Yellow
}

function Write-OK {
    param([string]$Text)
    Write-Host "  [OK] $Text" -ForegroundColor Green
}

function Write-Fail {
    param([string]$Text)
    Write-Host "  [FAIL] $Text" -ForegroundColor Red
}

function Get-FileWithProgress {
    param(
        [string]$Url,
        [string]$OutFile,
        [string]$DisplayName
    )
    
    Write-Step "Downloading $DisplayName..."
    
    try {
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $Url -OutFile $OutFile -UseBasicParsing
        $ProgressPreference = 'Continue'
        
        $size = (Get-Item $OutFile).Length
        $sizeMB = [math]::Round($size / 1MB, 2)
        Write-OK "$DisplayName - $sizeMB MB"
        return $true
    }
    catch {
        $errMsg = $_.Exception.Message
        Write-Fail "Failed to download $DisplayName"
        Write-Host "       Error: $errMsg" -ForegroundColor Gray
        return $false
    }
}

function Download-Model {
    param(
        [string]$ProviderName,
        [hashtable]$Config
    )
    
    Write-Header "Downloading $ProviderName Model"
    
    $targetDir = Join-Path $ModelsDir $Config.LocalFolder
    $modelFile = Join-Path $targetDir "model.onnx"
    
    # Check if already exists
    if ((Test-Path $modelFile) -and -not $Force) {
        Write-OK "Model already exists at $targetDir"
        Write-Host "        Use -Force to re-download" -ForegroundColor Gray
        return $true
    }
    
    # Create directory
    if (Test-Path $targetDir) {
        Write-Step "Cleaning existing directory..."
        Remove-Item $targetDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    Write-OK "Created $targetDir"
    
    $baseUrl = "$BaseUrl/$($Config.Subdirectory)"
    $success = $true
    
    # Download main model file
    $modelUrl = "$baseUrl/$($Config.ModelFile)"
    $modelDest = Join-Path $targetDir "model.onnx"
    if (-not (Get-FileWithProgress -Url $modelUrl -OutFile $modelDest -DisplayName "model.onnx")) {
        $success = $false
    }
    
    # Download model data file
    $dataUrl = "$baseUrl/$($Config.DataFile)"
    $dataDest = Join-Path $targetDir "model.onnx.data"
    if (-not (Get-FileWithProgress -Url $dataUrl -OutFile $dataDest -DisplayName "model.onnx.data")) {
        $success = $false
    }
    
    # Download config files
    Write-Step "Downloading configuration files..."
    $configSuccess = 0
    foreach ($configFile in $ConfigFiles) {
        $configUrl = "$baseUrl/$configFile"
        $configDest = Join-Path $targetDir $configFile
        
        try {
            $ProgressPreference = 'SilentlyContinue'
            Invoke-WebRequest -Uri $configUrl -OutFile $configDest -UseBasicParsing -ErrorAction Stop
            $ProgressPreference = 'Continue'
            $configSuccess++
        }
        catch {
            Write-Host "        (Skipped $configFile - not available)" -ForegroundColor Gray
        }
    }
    Write-OK "Downloaded $configSuccess config files"
    
    if ($success) {
        Write-Step "Verifying download..."
        $modelSize = (Get-Item $modelDest -ErrorAction SilentlyContinue).Length
        $dataSize = (Get-Item $dataDest -ErrorAction SilentlyContinue).Length
        
        if ($modelSize -gt 0 -and $dataSize -gt 0) {
            $totalMB = [math]::Round(($modelSize + $dataSize) / 1MB, 2)
            Write-OK "$ProviderName model ready! Total size: $totalMB MB"
            return $true
        }
        else {
            Write-Fail "Download verification failed - files are empty"
            return $false
        }
    }
    
    return $false
}

# Main script
Write-Header "SentinAI Phi-3 Model Downloader"
Write-Host "  Models Directory: $ModelsDir" -ForegroundColor Gray
Write-Host "  Provider: $Provider" -ForegroundColor Gray
Write-Host "  Force: $Force" -ForegroundColor Gray

if (-not (Test-Path $ModelsDir)) {
    New-Item -ItemType Directory -Path $ModelsDir -Force | Out-Null
    Write-OK "Created models directory"
}

$results = @{}

if ($Provider -eq "CPU" -or $Provider -eq "Both") {
    $results["CPU"] = Download-Model -ProviderName "CPU" -Config $Models["CPU"]
}

if ($Provider -eq "DirectML" -or $Provider -eq "Both") {
    $results["DirectML"] = Download-Model -ProviderName "DirectML" -Config $Models["DirectML"]
}

Write-Header "Download Summary"

foreach ($key in $results.Keys) {
    if ($results[$key]) {
        $status = "[OK] Ready"
        $color = "Green"
    } else {
        $status = "[FAIL] Failed"
        $color = "Red"
    }
    $path = Join-Path $ModelsDir $Models[$key].LocalFolder
    Write-Host "  $key : " -NoNewline
    Write-Host $status -ForegroundColor $color
    Write-Host "        Path: $path" -ForegroundColor Gray
}

Write-Host ""
Write-Host "  To configure, edit appsettings.json:" -ForegroundColor Yellow
Write-Host "    Brain.ExecutionProvider = CPU or DirectML" -ForegroundColor Gray
Write-Host ""

$failCount = 0
foreach ($val in $results.Values) {
    if (-not $val) { $failCount++ }
}

if ($failCount -eq 0) {
    Write-OK "All models downloaded successfully!"
    exit 0
}
else {
    Write-Fail "Some downloads failed"
    exit 1
}
