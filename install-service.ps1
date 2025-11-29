# SentinAI Service Installation Script
# Installs the Sentinel Service as a Windows Service

#Requires -RunAsAdministrator

param(
    [string]$Configuration = "Release",
    [switch]$Uninstall
)

$ServiceName = "SentinAISentinel"
$DisplayName = "SentinAI Sentinel Service"
$Description = "Background service for SentinAI autonomous storage management"

# Try .NET 9 first, fallback to .NET 8
$ServicePathNet9 = Join-Path $PSScriptRoot "src\SentinAI.SentinelService\bin\$Configuration\net9.0-windows10.0.22621.0\win-x64\SentinAI.SentinelService.exe"
$ServicePathNet8 = Join-Path $PSScriptRoot "src\SentinAI.SentinelService\bin\$Configuration\net8.0-windows10.0.22621.0\win-x64\SentinAI.SentinelService.exe"

if (Test-Path $ServicePathNet9) {
    $ServicePath = $ServicePathNet9
    Write-Host "✓ Using .NET 9 build" -ForegroundColor Green
} elseif (Test-Path $ServicePathNet8) {
    $ServicePath = $ServicePathNet8
    Write-Host "✓ Using .NET 8 build" -ForegroundColor Green
} else {
    $ServicePath = $ServicePathNet9  # Default to .NET 9 for error message
}

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  SentinAI Service Installer" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

if ($Uninstall) {
    Write-Host "Uninstalling service..." -ForegroundColor Yellow

    # Stop service if running
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service) {
        if ($service.Status -eq 'Running') {
            Write-Host "Stopping service..." -ForegroundColor Yellow
            Stop-Service -Name $ServiceName -Force
        }

        # Delete service
        Write-Host "Removing service..." -ForegroundColor Yellow
        sc.exe delete $ServiceName

        if ($LASTEXITCODE -eq 0) {
            Write-Host "✓ Service uninstalled successfully" -ForegroundColor Green
        } else {
            Write-Host "ERROR: Failed to uninstall service" -ForegroundColor Red
            exit 1
        }
    } else {
        Write-Host "Service not found (already uninstalled)" -ForegroundColor Yellow
    }
} else {
    # Install service
    Write-Host "Checking service executable..." -ForegroundColor Yellow

    if (-not (Test-Path $ServicePath)) {
        Write-Host "ERROR: Service executable not found at:" -ForegroundColor Red
        Write-Host "  $ServicePath" -ForegroundColor Red
        Write-Host "" -ForegroundColor Red
        Write-Host "Please build the solution first:" -ForegroundColor Yellow
        Write-Host "  .\build.ps1 -Configuration $Configuration" -ForegroundColor Cyan
        exit 1
    }

    Write-Host "✓ Service executable found" -ForegroundColor Green

    # Check if service already exists
    $existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existingService) {
        Write-Host "Service already exists. Stopping and removing..." -ForegroundColor Yellow
        if ($existingService.Status -eq 'Running') {
            Stop-Service -Name $ServiceName -Force
        }
        sc.exe delete $ServiceName
        Start-Sleep -Seconds 2
    }

    Write-Host "Creating service..." -ForegroundColor Yellow
    sc.exe create $ServiceName binPath= "`"$ServicePath`"" DisplayName= $DisplayName start= auto

    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Failed to create service" -ForegroundColor Red
        exit 1
    }

    Write-Host "Setting service description..." -ForegroundColor Yellow
    sc.exe description $ServiceName $Description

    Write-Host "Starting service..." -ForegroundColor Yellow
    sc.exe start $ServiceName

    if ($LASTEXITCODE -eq 0) {
        Write-Host ""
        Write-Host "============================================" -ForegroundColor Cyan
        Write-Host "  Service installed successfully!" -ForegroundColor Green
        Write-Host "============================================" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "Service Name: $ServiceName" -ForegroundColor Cyan
        Write-Host "Display Name: $DisplayName" -ForegroundColor Cyan
        Write-Host "Status: Running" -ForegroundColor Green
        Write-Host ""
        Write-Host "To stop the service:" -ForegroundColor Yellow
        Write-Host "  sc.exe stop $ServiceName" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "To uninstall the service:" -ForegroundColor Yellow
        Write-Host "  .\install-service.ps1 -Uninstall" -ForegroundColor Cyan
        Write-Host ""
    } else {
        Write-Host "ERROR: Failed to start service" -ForegroundColor Red
        Write-Host "Check Event Viewer for error details" -ForegroundColor Yellow
        exit 1
    }
}

