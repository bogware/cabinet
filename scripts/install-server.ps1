#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Cabinet API Server Installer for Windows

.DESCRIPTION
    Installs Cabinet API as a Windows Service with proper configuration.
    Also installs the CLI tool and optionally the forwarding agent.

.PARAMETER InstallDir
    Installation directory (default: C:\Program Files\Cabinet)

.PARAMETER Port
    HTTP port for the API server (default: 8080)

.PARAMETER Storage
    Storage backend: sqlite, postgresql, opensearch (default: sqlite)

.PARAMETER ConnectionString
    Storage connection string (auto-generated for SQLite)

.PARAMETER RunWizard
    Launch the interactive setup wizard after install

.EXAMPLE
    .\install-server.ps1
    .\install-server.ps1 -Port 5000 -Storage postgresql
    .\install-server.ps1 -RunWizard
#>

param(
    [string]$InstallDir = "$env:ProgramFiles\Cabinet",
    [string]$DataDir = "$env:ProgramData\Cabinet",
    [int]$Port = 8080,
    [ValidateSet("sqlite", "postgresql", "opensearch")]
    [string]$Storage = "sqlite",
    [string]$ConnectionString = "",
    [switch]$RunWizard,
    [switch]$IncludeAgent
)

$ErrorActionPreference = "Stop"

# ─── Banner ────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "  +=======================================+" -ForegroundColor Cyan
Write-Host "  |   Cabinet API Server Installer        |" -ForegroundColor Cyan
Write-Host "  |   Log Intelligence System - Windows   |" -ForegroundColor Cyan
Write-Host "  +=======================================+" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Install dir: $InstallDir"
Write-Host "  Data dir:    $DataDir"
Write-Host "  Port:        $Port"
Write-Host "  Storage:     $Storage"
Write-Host ""

# ─── Create directories ───────────────────────────────────────────────
Write-Host "[1/6] Creating directories" -ForegroundColor Green

$dirs = @(
    "$InstallDir\api",
    "$InstallDir\cli",
    "$DataDir\db",
    "$DataDir\backups",
    "$DataDir\logs"
)

if ($IncludeAgent) {
    $dirs += "$InstallDir\agent"
    $dirs += "$DataDir\agent\spool"
    $dirs += "$DataDir\agent\data"
}

foreach ($dir in $dirs) {
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
}

# ─── Check for binaries ───────────────────────────────────────────────
Write-Host "[2/6] Checking for Cabinet binaries" -ForegroundColor Green

$binaryFound = $false

# Check if API binary exists in install dir or current dir
if (Test-Path "$InstallDir\api\Cabinet.API.exe") {
    Write-Host "  Found existing API binary"
    $binaryFound = $true
}
elseif (Test-Path ".\Cabinet.API.exe") {
    Write-Host "  Copying API binary from current directory"
    Copy-Item ".\Cabinet.API*" "$InstallDir\api\" -Force
    Copy-Item ".\appsettings*" "$InstallDir\api\" -Force -ErrorAction SilentlyContinue
    if (Test-Path ".\wwwroot") {
        Copy-Item ".\wwwroot" "$InstallDir\api\" -Recurse -Force
    }
    $binaryFound = $true
}

# Check for CLI
if (Test-Path ".\cabinet.exe") {
    Write-Host "  Copying CLI binary from current directory"
    Copy-Item ".\cabinet*" "$InstallDir\cli\" -Force
}

# Check for Agent
if ($IncludeAgent -and (Test-Path ".\cabinet-agent.exe")) {
    Write-Host "  Copying Agent binary from current directory"
    Copy-Item ".\cabinet-agent*" "$InstallDir\agent\" -Force
}

if (-not $binaryFound) {
    Write-Host ""
    Write-Host "  NOTE: No Cabinet.API.exe binary found." -ForegroundColor Yellow
    Write-Host "  After installation, copy the binary for win-x64 to:"
    Write-Host "    $InstallDir\api\"
    Write-Host ""
    Write-Host "  Download from: https://github.com/draxios/cabinet/releases" -ForegroundColor Cyan
    Write-Host ""
}

# ─── Generate configuration ───────────────────────────────────────────
Write-Host "[3/6] Generating configuration" -ForegroundColor Green

# Storage config
switch ($Storage) {
    "sqlite" {
        $storageType = "SQLite"
        if (-not $ConnectionString) { $ConnectionString = "Data Source=$DataDir\db\cabinet.db" }
    }
    "postgresql" {
        $storageType = "PostgreSQL"
        if (-not $ConnectionString) { $ConnectionString = "SET_VIA_ENV_VAR" }
    }
    "opensearch" {
        $storageType = "OpenSearch"
        if (-not $ConnectionString) { $ConnectionString = "SET_VIA_ENV_VAR" }
    }
}

$configPath = "$DataDir\appsettings.json"
if (-not (Test-Path $configPath)) {
    $config = @{
        Urls = "http://0.0.0.0:$Port"
        Storage = @{
            Type = $storageType
            ConnectionString = $ConnectionString
        }
        Logging = @{
            LogLevel = @{
                Default = "Information"
                "Microsoft.AspNetCore" = "Warning"
            }
        }
        RateLimiting = @{
            Basic = @{ RequestsPerMinute = 100 }
            Professional = @{ RequestsPerMinute = 1000 }
            Enterprise = @{ RequestsPerMinute = 10000 }
        }
        Cors = @{
            AllowedOrigins = @("http://localhost:3000")
        }
        Backup = @{
            ArchivePath = "$DataDir\backups"
            RetentionDays = 30
            AutoBackupEnabled = $false
        }
    } | ConvertTo-Json -Depth 5

    Set-Content -Path $configPath -Value $config -Encoding UTF8
    Write-Host "  Configuration written to $configPath"
}
else {
    Write-Host "  Configuration already exists at $configPath"
}

# Copy config to API dir so the binary finds it
Copy-Item $configPath "$InstallDir\api\appsettings.Production.json" -Force

# ─── Install Windows Service ──────────────────────────────────────────
Write-Host "[4/6] Installing Windows Service" -ForegroundColor Green

$serviceName = "CabinetAPI"
$serviceDisplayName = "Cabinet API Server"
$serviceDescription = "Cabinet Log Intelligence System - REST API Server"
$binaryPath = "$InstallDir\api\Cabinet.API.exe --urls http://0.0.0.0:$Port"

# Stop existing service if running
$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host "  Stopping existing service..."
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 1
}

if ($binaryFound) {
    # Create the service
    New-Service -Name $serviceName `
        -BinaryPathName $binaryPath `
        -DisplayName $serviceDisplayName `
        -Description $serviceDescription `
        -StartupType Automatic | Out-Null

    # Set environment variables for the service
    $regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
    $env = @(
        "ASPNETCORE_ENVIRONMENT=Production",
        "DOTNET_ENVIRONMENT=Production",
        "Storage__ConnectionString=$ConnectionString"
    )
    Set-ItemProperty -Path $regPath -Name "Environment" -Value $env -Type MultiString

    Write-Host "  Windows Service installed: $serviceName"
}
else {
    Write-Host "  Skipping service creation (no binary found)" -ForegroundColor Yellow
}

# ─── Install Agent Service ────────────────────────────────────────────
if ($IncludeAgent) {
    Write-Host "[4b] Installing Agent Windows Service" -ForegroundColor Green

    $agentServiceName = "CabinetAgent"
    $existingAgent = Get-Service -Name $agentServiceName -ErrorAction SilentlyContinue
    if ($existingAgent) {
        Stop-Service -Name $agentServiceName -Force -ErrorAction SilentlyContinue
        sc.exe delete $agentServiceName | Out-Null
        Start-Sleep -Seconds 1
    }

    if (Test-Path "$InstallDir\agent\cabinet-agent.exe") {
        New-Service -Name $agentServiceName `
            -BinaryPathName "$InstallDir\agent\cabinet-agent.exe" `
            -DisplayName "Cabinet Log Forwarding Agent" `
            -Description "Cabinet Agent - Monitors log files and forwards to Cabinet API" `
            -StartupType Automatic | Out-Null

        Write-Host "  Agent service installed: $agentServiceName"
    }
}

# ─── Add to PATH ──────────────────────────────────────────────────────
Write-Host "[5/6] Configuring PATH" -ForegroundColor Green

$cliPath = "$InstallDir\cli"
$currentPath = [Environment]::GetEnvironmentVariable("Path", "Machine")
if ($currentPath -notlike "*$cliPath*") {
    [Environment]::SetEnvironmentVariable("Path", "$currentPath;$cliPath", "Machine")
    Write-Host "  Added $cliPath to system PATH"
    Write-Host "  (restart terminal to use 'cabinet' command)"
}
else {
    Write-Host "  CLI already in PATH"
}

# ─── Firewall rule ────────────────────────────────────────────────────
Write-Host "[6/6] Configuring firewall" -ForegroundColor Green

$ruleName = "Cabinet API Server (TCP $Port)"
$existingRule = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
if (-not $existingRule) {
    New-NetFirewallRule -DisplayName $ruleName `
        -Direction Inbound -Protocol TCP -LocalPort $Port `
        -Action Allow -Profile Domain,Private | Out-Null
    Write-Host "  Firewall rule created for port $Port (Domain + Private networks)"
}
else {
    Write-Host "  Firewall rule already exists"
}

# ─── Done ──────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "  Installation complete!" -ForegroundColor Green
Write-Host ""
Write-Host "  +─────────────────────────────────────────────────+" -ForegroundColor Cyan
Write-Host "  |  Quick Start                                    |" -ForegroundColor Cyan
Write-Host "  +─────────────────────────────────────────────────+" -ForegroundColor Cyan
Write-Host "  |  Start:     Start-Service CabinetAPI            |"
Write-Host "  |  Stop:      Stop-Service CabinetAPI             |"
Write-Host "  |  Status:    Get-Service CabinetAPI              |"
Write-Host "  |  Logs:      Get-EventLog -LogName Application   |"
Write-Host "  |  Dashboard: http://localhost:$Port/              |"
Write-Host "  |  API Docs:  http://localhost:$Port/swagger       |"
Write-Host "  +─────────────────────────────────────────────────+" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Config:   $DataDir\appsettings.json"
Write-Host "  Data:     $DataDir\"
Write-Host "  Binaries: $InstallDir\"
Write-Host ""

if (-not $binaryFound) {
    Write-Host "  IMPORTANT: Copy Cabinet.API.exe to $InstallDir\api\ before starting." -ForegroundColor Yellow
    Write-Host ""
}

# Run wizard if requested
if ($RunWizard -and (Test-Path "$InstallDir\cli\cabinet.exe")) {
    Write-Host "  Launching setup wizard..."
    & "$InstallDir\cli\cabinet.exe" setup
}
