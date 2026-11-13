#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Cabinet Agent Installer for Windows

.DESCRIPTION
    Installs Cabinet Log Forwarding Agent as a Windows Service.
    The agent monitors log files and forwards events to a Cabinet API server.

.PARAMETER ServerUrl
    URL of the Cabinet API server (e.g., https://cabinet.example.com)

.PARAMETER ApiKey
    API key for authenticating with the Cabinet server

.PARAMETER Paths
    Comma-separated log file paths/globs to monitor (e.g., "C:\logs\*.log,C:\inetpub\logs\*.log")

.PARAMETER InstallDir
    Installation directory (default: C:\Program Files\Cabinet\agent)

.EXAMPLE
    .\install-agent.ps1 -ServerUrl https://cabinet.example.com -ApiKey "cab_yourkey"
    .\install-agent.ps1 -ServerUrl https://cabinet.example.com:8443 -ApiKey "cab_yourkey" -Paths "C:\logs\*.log"
#>

param(
    [string]$ServerUrl = "",
    [string]$ApiKey = "",
    [string]$Paths = "",
    # The server's certificate fingerprint (https-fingerprint.txt on the server), for a self-signed certificate
    [string]$CertificateSha256 = "",
    [string]$InstallDir = "$env:ProgramFiles\Cabinet\agent",
    [string]$DataDir = "$env:ProgramData\Cabinet\agent"
)

$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "  +=======================================+" -ForegroundColor Cyan
Write-Host "  |   Cabinet Agent Installer             |" -ForegroundColor Cyan
Write-Host "  |   Log Forwarding Agent - Windows      |" -ForegroundColor Cyan
Write-Host "  +=======================================+" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Install dir: $InstallDir"
Write-Host "  Data dir:    $DataDir"
Write-Host ""

# Prompt for required values if not provided
if (-not $ServerUrl) {
    $ServerUrl = Read-Host "  Enter Cabinet server URL (e.g., https://cabinet.example.com:8443)"
    if (-not $ServerUrl) { throw "A server URL is required." }
}
if (-not $ApiKey) {
    $ApiKey = Read-Host "  Enter the agent's API key (an Ingest key from the server)"
    if (-not $ApiKey) { throw "An API key is required." }
}
if ($ServerUrl -like "http://*") {
    Write-Host "  NOTE: over http the key and events cross the network in the clear, and the agent won't update itself." -ForegroundColor Yellow
}

# Administrators and SYSTEM only: the settings hold the key, and anything in the spool is sent as this agent
function Protect-Path([string]$Path, [bool]$Folder) {
    $acl = if ($Folder) { New-Object System.Security.AccessControl.DirectorySecurity } else { New-Object System.Security.AccessControl.FileSecurity }
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($who in @("BUILTIN\Administrators", "NT AUTHORITY\SYSTEM")) {
        $rule = if ($Folder) { New-Object System.Security.AccessControl.FileSystemAccessRule($who, "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow") }
                else { New-Object System.Security.AccessControl.FileSystemAccessRule($who, "FullControl", "Allow") }
        $acl.AddAccessRule($rule)
    }
    Set-Acl -Path $Path -AclObject $acl
}
if (-not $Paths) {
    $Paths = Read-Host "  Enter log paths to monitor (comma-separated, e.g., C:\logs\*.log)"
    if (-not $Paths) { $Paths = "C:\Windows\System32\LogFiles\**\*.log" }
}

# ─── Create directories ───────────────────────────────────────────────
Write-Host "[1/4] Creating directories" -ForegroundColor Green
@($InstallDir, "$DataDir\spool", "$DataDir\data") | ForEach-Object {
    if (-not (Test-Path $_)) { New-Item -ItemType Directory -Path $_ -Force | Out-Null }
}
Protect-Path $DataDir $true

# ─── Check for binary ─────────────────────────────────────────────────
Write-Host "[2/4] Checking for agent binary" -ForegroundColor Green
$binaryFound = $false

if (Test-Path "$InstallDir\cabinet-agent.exe") {
    $binaryFound = $true
    Write-Host "  Found existing agent binary"
}
elseif (Test-Path ".\cabinet-agent.exe") {
    Copy-Item ".\cabinet-agent*" "$InstallDir\" -Force
    $binaryFound = $true
    Write-Host "  Copied agent binary from current directory"
}
else {
    Write-Host "  NOTE: No cabinet-agent.exe found." -ForegroundColor Yellow
    Write-Host "  Download from: https://github.com/bogware/cabinet/releases"
}

# ─── Generate configuration ───────────────────────────────────────────
Write-Host "[3/4] Generating configuration" -ForegroundColor Green

$configPath = "$InstallDir\appsettings.json"
if (-not (Test-Path $configPath)) {
    # Build paths array
    $pathsList = $Paths -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }
    $pathsJson = ($pathsList | ForEach-Object { "`"$($_ -replace '\\', '\\')`"" }) -join ", "

    $configContent = @"
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Cabinet.Agent": "Information"
    }
  },
  "Agent": {
    "Inputs": [
      {
        "Paths": [$pathsJson],
        "Recursive": false,
        "SourceType": "generic",
        "Index": "main",
        "ExcludePatterns": ["*.gz", "*.zip", "*.bz2", "*.tmp"]
      }
    ],
    "Output": {
      "ServerUrl": "$ServerUrl",
      "ApiKey": "$ApiKey",
      "BatchSize": 100,
      "FlushIntervalSeconds": 5,
      "MaxRetries": 5,
      "TimeoutSeconds": 30,
      "VerifySsl": true,
      "CertificateSha256": "$CertificateSha256",
      "MaxBufferSize": 10000,
      "SpillDirectory": "$($DataDir -replace '\\', '\\')\spool"
    },
    "Registry": {
      "Path": "$($DataDir -replace '\\', '\\')\data\registry.json",
      "SaveIntervalSeconds": 10
    },
    "ScanIntervalSeconds": 10,
    "Hostname": "$env:COMPUTERNAME"
  }
}
"@
    Set-Content -Path $configPath -Value $configContent -Encoding UTF8
    Write-Host "  Configuration written to $configPath"
}
else {
    Write-Host "  Configuration already exists at $configPath"
}
Protect-Path $configPath $false

# ─── Install Windows Service ──────────────────────────────────────────
Write-Host "[4/4] Installing Windows Service" -ForegroundColor Green

$serviceName = "CabinetAgent"
$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existingService) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 1
}

if ($binaryFound) {
    New-Service -Name $serviceName `
        -BinaryPathName "$InstallDir\cabinet-agent.exe" `
        -DisplayName "Cabinet Log Forwarding Agent" `
        -Description "Monitors log files and forwards events to Cabinet API server" `
        -StartupType Automatic | Out-Null

    Write-Host "  Service installed: $serviceName"
}
else {
    Write-Host "  Skipping service creation (no binary)" -ForegroundColor Yellow
}

# ─── Done ──────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "  Installation complete!" -ForegroundColor Green
Write-Host ""
Write-Host "  Start:   Start-Service CabinetAgent"
Write-Host "  Stop:    Stop-Service CabinetAgent"
Write-Host "  Status:  Get-Service CabinetAgent"
Write-Host "  Config:  $configPath"
Write-Host ""

if ($binaryFound) {
    $startNow = Read-Host "  Start agent now? (y/N)"
    if ($startNow -eq 'y') {
        Start-Service CabinetAgent
        Write-Host "  Agent started!" -ForegroundColor Green
    }
}
