# MoltbookAgent Service Installation Script
# Run this script as Administrator

param(
    [Parameter(Mandatory=$false)]
    [string]$Action = "install"
)

$ServiceName = "MoltbookAgent"
$ProjectPath = $PSScriptRoot
$PublishPath = Join-Path $ProjectPath "publish"
$ExePath = Join-Path $PublishPath "MoltbookAgent.exe"

function Install-Service {
    Write-Host "Building and publishing MoltbookAgent..." -ForegroundColor Cyan

    # Build and publish
    dotnet publish -c Release -o $PublishPath

    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed!" -ForegroundColor Red
        exit 1
    }

    Write-Host "Build successful!" -ForegroundColor Green

    # Check if service already exists
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

    if ($service) {
        Write-Host "Service already exists. Use 'update' action to update it." -ForegroundColor Yellow
        return
    }

    Write-Host "Installing Windows Service..." -ForegroundColor Cyan

    # Create the service
    sc.exe create $ServiceName binPath= $ExePath start= auto

    if ($LASTEXITCODE -eq 0) {
        Write-Host "Service installed successfully!" -ForegroundColor Green
        Write-Host "Starting service..." -ForegroundColor Cyan
        sc.exe start $ServiceName

        if ($LASTEXITCODE -eq 0) {
            Write-Host "Service started!" -ForegroundColor Green
        } else {
            Write-Host "Failed to start service. Check logs for details." -ForegroundColor Red
        }
    } else {
        Write-Host "Failed to install service!" -ForegroundColor Red
    }
}

function Update-Service {
    Write-Host "Updating MoltbookAgent service..." -ForegroundColor Cyan

    # Stop the service if running
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

    if ($service -and $service.Status -eq 'Running') {
        Write-Host "Stopping service..." -ForegroundColor Cyan
        sc.exe stop $ServiceName
        Start-Sleep -Seconds 2
    }

    # Build and publish
    Write-Host "Building new version..." -ForegroundColor Cyan
    dotnet publish -c Release -o $PublishPath

    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed!" -ForegroundColor Red
        exit 1
    }

    Write-Host "Build successful!" -ForegroundColor Green

    # Start the service
    Write-Host "Starting service..." -ForegroundColor Cyan
    sc.exe start $ServiceName

    if ($LASTEXITCODE -eq 0) {
        Write-Host "Service updated and started!" -ForegroundColor Green
    } else {
        Write-Host "Failed to start service. Check logs for details." -ForegroundColor Red
    }
}

function Uninstall-Service {
    Write-Host "Uninstalling MoltbookAgent service..." -ForegroundColor Cyan

    # Stop the service if running
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

    if ($service) {
        if ($service.Status -eq 'Running') {
            Write-Host "Stopping service..." -ForegroundColor Cyan
            sc.exe stop $ServiceName
            Start-Sleep -Seconds 2
        }

        Write-Host "Deleting service..." -ForegroundColor Cyan
        sc.exe delete $ServiceName

        if ($LASTEXITCODE -eq 0) {
            Write-Host "Service uninstalled successfully!" -ForegroundColor Green
        } else {
            Write-Host "Failed to uninstall service!" -ForegroundColor Red
        }
    } else {
        Write-Host "Service not found!" -ForegroundColor Yellow
    }
}

function Get-ServiceStatus {
    Write-Host "MoltbookAgent Service Status:" -ForegroundColor Cyan
    sc.exe query $ServiceName
}

function Start-ServiceManual {
    Write-Host "Starting MoltbookAgent service..." -ForegroundColor Cyan
    sc.exe start $ServiceName

    if ($LASTEXITCODE -eq 0) {
        Write-Host "Service started!" -ForegroundColor Green
    } else {
        Write-Host "Failed to start service!" -ForegroundColor Red
    }
}

function Stop-ServiceManual {
    Write-Host "Stopping MoltbookAgent service..." -ForegroundColor Cyan
    sc.exe stop $ServiceName

    if ($LASTEXITCODE -eq 0) {
        Write-Host "Service stopped!" -ForegroundColor Green
    } else {
        Write-Host "Failed to stop service!" -ForegroundColor Red
    }
}

# Check if running as administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "This script must be run as Administrator!" -ForegroundColor Red
    exit 1
}

# Execute action
switch ($Action.ToLower()) {
    "install" { Install-Service }
    "update" { Update-Service }
    "uninstall" { Uninstall-Service }
    "status" { Get-ServiceStatus }
    "start" { Start-ServiceManual }
    "stop" { Stop-ServiceManual }
    default {
        Write-Host "Usage: install-service.ps1 -Action <install|update|uninstall|status|start|stop>" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "Actions:" -ForegroundColor Cyan
        Write-Host "  install   - Build and install the service"
        Write-Host "  update    - Rebuild and update existing service"
        Write-Host "  uninstall - Stop and remove the service"
        Write-Host "  status    - Show service status"
        Write-Host "  start     - Start the service"
        Write-Host "  stop      - Stop the service"
    }
}
