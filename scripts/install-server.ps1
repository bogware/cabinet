#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs the Cabinet API server as a Windows service.

.DESCRIPTION
    Run from the folder you extracted cabinet-win-x64.zip into (the one holding api\, cli\ and
    agent\), with this script beside it or in it. It copies the binaries, writes the settings,
    creates API keys on first install, installs and starts the CabinetAPI service, and opens the
    port in the firewall to the addresses you list.

    Running it again upgrades the binaries and keeps the settings and keys.

.PARAMETER Port
    HTTP port (default 8080).

.PARAMETER AllowFrom
    Addresses or ranges allowed through the firewall, such as your game servers' IPs or a
    Tailscale range (100.64.0.0/10). Without it, only this machine can connect.

.PARAMETER IngestKeys
    Names for ingest-only keys to create on first install, one per sender, such as l4d2-dev.

.PARAMETER Https
    Serve HTTPS on -HttpsPort (default 8443) with a self-signed certificate made here, for agents
    on other networks. Plain HTTP on -Port then answers only this machine, for the dashboard.
    Prints the certificate's fingerprint, which each agent pins (Output:CertificateSha256).

.PARAMETER Gaming
    Turn on the game log parsers (Left 4 Dead 2, SourceMod, SteamCMD).

.PARAMETER RetentionDays
    Delete events older than this many days (default 90, 0 keeps everything).

.EXAMPLE
    .\install-server.ps1 -Gaming -IngestKeys game-dev,game-prod -Https -AllowFrom 203.0.113.10,203.0.113.20
#>

param(
    [string]$InstallDir = "$env:ProgramFiles\Cabinet",
    [string]$DataDir = "$env:ProgramData\Cabinet",
    [int]$Port = 8080,
    [ValidateSet("sqlite", "postgresql")]
    [string]$Storage = "sqlite",
    [string]$ConnectionString = "",
    [string[]]$AllowFrom = @(),
    [string[]]$IngestKeys = @(),
    [switch]$Gaming,
    [switch]$Https,
    [int]$HttpsPort = 8443,
    [int]$RetentionDays = 90,
    [switch]$IncludeAgent
)

$ErrorActionPreference = "Stop"
$serviceName = "CabinetAPI"

function New-Key {
    $bytes = New-Object byte[] 32
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    return -join ($bytes | ForEach-Object { $_.ToString("x2") })
}

# Readable only by administrators and the service, since it holds the keys
function Protect-File([string]$Path) {
    $acl = New-Object System.Security.AccessControl.FileSecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($who in @("BUILTIN\Administrators", "NT AUTHORITY\SYSTEM")) {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($who, "FullControl", "Allow")))
    }
    Set-Acl -Path $Path -AclObject $acl
}

Write-Host ""
Write-Host "Cabinet API server installer" -ForegroundColor Cyan
Write-Host "  Install: $InstallDir"
Write-Host "  Data:    $DataDir"
Write-Host "  Port:    $Port"
Write-Host ""

# --- Find the binaries
Write-Host "[1/6] Finding binaries" -ForegroundColor Green
$source = $null
foreach ($candidate in @((Get-Location).Path, $PSScriptRoot, (Split-Path $PSScriptRoot -Parent))) {
    if (-not $candidate) { continue }
    if (Test-Path (Join-Path $candidate "api\Cabinet.API.exe")) { $source = $candidate; break }
}
if (-not $source) {
    throw "Cabinet.API.exe not found. Extract cabinet-win-x64.zip and run this from that folder, which holds api\, cli\ and agent\."
}
Write-Host "  From $source"

# --- Stop the service while files are replaced -------------------------
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne "Stopped") {
    Write-Host "  Stopping $serviceName"
    Stop-Service -Name $serviceName -Force
    $existing.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
}

# --- Copy-------------
Write-Host "[2/6] Copying files" -ForegroundColor Green
foreach ($dir in @("$InstallDir\api", "$InstallDir\cli", "$DataDir\db", "$DataDir\backups")) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}
# Settings live in $DataDir and are kept on upgrade
Get-ChildItem "$source\api" | Where-Object { $_.Name -ne "appsettings.Production.json" } |
    Copy-Item -Destination "$InstallDir\api" -Recurse -Force
if (Test-Path "$source\cli") { Copy-Item "$source\cli\*" "$InstallDir\cli" -Recurse -Force }
if ($IncludeAgent -and (Test-Path "$source\agent")) {
    New-Item -ItemType Directory -Path "$InstallDir\agent" -Force | Out-Null
    Copy-Item "$source\agent\*" "$InstallDir\agent" -Recurse -Force
}

# --- Settings and keys
Write-Host "[3/6] Settings" -ForegroundColor Green
$settingsPath = "$DataDir\appsettings.Production.json"
$keysPath = "$DataDir\api-keys.txt"
if (Test-Path $settingsPath) {
    Write-Host "  Keeping existing settings and keys in $settingsPath"
}
else {
    if ($Storage -eq "sqlite") {
        $storageType = "SQLite"
        if (-not $ConnectionString) { $ConnectionString = "Data Source=$DataDir\db\cabinet.db" }
    }
    else {
        $storageType = "PostgreSQL"
        if (-not $ConnectionString) { throw "-ConnectionString is required for PostgreSQL" }
    }

    $keys = @(@{ Key = (New-Key); Tier = "Admin"; Name = "admin" })
    foreach ($name in $IngestKeys) {
        $keys += @{ Key = (New-Key); Tier = "Ingest"; Name = $name }
    }

    $settings = [ordered]@{
        Storage = [ordered]@{
            Type = $storageType
            ConnectionString = $ConnectionString
            RetentionDays = $RetentionDays
        }
        ApiKeys = @{ Keys = $keys }
        Gaming = @{ Enabled = [bool]$Gaming }
        Backup = @{ ArchivePath = "$DataDir\backups" }
    }
    $settings | ConvertTo-Json -Depth 6 | Set-Content -Path $settingsPath -Encoding UTF8
    Protect-File $settingsPath

    $lines = @("# Cabinet API keys. The admin key is for the dashboard; each other key is for one sender.")
    foreach ($key in $keys) { $lines += "$($key.Name) ($($key.Tier)): $($key.Key)" }
    Set-Content -Path $keysPath -Value $lines -Encoding UTF8
    Protect-File $keysPath
    Write-Host "  Created $settingsPath"
    Write-Host "  Keys written to $keysPath (administrators only)"
}
# --- HTTPS ------------------------------------------------------------
$fingerprint = $null
$pfxPath = "$DataDir\https.pfx"
$current = Get-Content $settingsPath -Raw | ConvertFrom-Json
if ($Https -and (-not $current.Kestrel -or -not (Test-Path $pfxPath))) {
    $password = New-Key
    $names = @("cabinet", "localhost", $env:COMPUTERNAME) | Select-Object -Unique
    $certificate = New-SelfSignedCertificate -Subject "CN=cabinet" -DnsName $names `
        -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddYears(10) `
        -KeyAlgorithm RSA -KeyLength 2048 -KeyExportPolicy Exportable -FriendlyName "Cabinet HTTPS"
    Export-PfxCertificate -Cert $certificate -FilePath $pfxPath `
        -Password (ConvertTo-SecureString $password -AsPlainText -Force) | Out-Null
    Remove-Item "Cert:\LocalMachine\My\$($certificate.Thumbprint)"
    Protect-File $pfxPath

    $kestrel = @{
        Endpoints = @{
            Http = @{ Url = "http://localhost:$Port" }
            Https = @{ Url = "https://0.0.0.0:$HttpsPort"; Certificate = @{ Path = $pfxPath; Password = $password } }
        }
    }
    $current | Add-Member -NotePropertyName Kestrel -NotePropertyValue $kestrel -Force
    $current | ConvertTo-Json -Depth 8 | Set-Content -Path $settingsPath -Encoding UTF8
    Protect-File $settingsPath
    $current = Get-Content $settingsPath -Raw | ConvertFrom-Json
    Write-Host "  Created a self-signed certificate, valid 10 years: $pfxPath"
}
$useKestrel = [bool]$current.Kestrel
if ($useKestrel) {
    $httpsEndpoint = $current.Kestrel.Endpoints.Https
    $HttpsPort = ([Uri]($httpsEndpoint.Url -replace "0\.0\.0\.0", "localhost")).Port
    $loaded = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($httpsEndpoint.Certificate.Path, $httpsEndpoint.Certificate.Password)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $fingerprint = -join ($sha.ComputeHash($loaded.RawData) | ForEach-Object { $_.ToString("X2") })
    Set-Content -Path "$DataDir\https-fingerprint.txt" -Value $fingerprint -Encoding ASCII
}

Copy-Item $settingsPath "$InstallDir\api\appsettings.Production.json" -Force
Protect-File "$InstallDir\api\appsettings.Production.json"

# --- Service----------
Write-Host "[4/6] Service" -ForegroundColor Green
# With HTTPS, the addresses come from the Kestrel section of the settings
$binaryPath = if ($useKestrel) { "`"$InstallDir\api\Cabinet.API.exe`"" } else { "`"$InstallDir\api\Cabinet.API.exe`" --urls http://0.0.0.0:$Port" }
if (-not $existing) {
    New-Service -Name $serviceName -BinaryPathName $binaryPath -DisplayName "Cabinet API Server" `
        -Description "Cabinet log search and detection" -StartupType Automatic | Out-Null
    Write-Host "  Created $serviceName"
}
else {
    # Directly, since Windows PowerShell mangles quoted paths passed to sc.exe
    Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name ImagePath -Value $binaryPath
}
# Restart after a crash: 5 s, 30 s, then every 60 s
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name Environment `
    -Type MultiString -Value @("ASPNETCORE_ENVIRONMENT=Production", "DOTNET_ENVIRONMENT=Production")

# --- Firewall---------
Write-Host "[5/6] Firewall" -ForegroundColor Green
# Remove rules from earlier installs, then open only the port agents use
Get-NetFirewallRule -DisplayName "Cabinet API (TCP *" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
$openPort = if ($useKestrel) { $HttpsPort } else { $Port }
$ruleName = "Cabinet API (TCP $openPort)"
if ($AllowFrom.Count -gt 0) {
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $openPort `
        -RemoteAddress $AllowFrom -Action Allow -Profile Any | Out-Null
    Write-Host "  Port $openPort open to: $($AllowFrom -join ', ')"
}
else {
    Write-Host "  No -AllowFrom given: only this machine can connect"
}

# --- PATH and start---
Write-Host "[6/6] Starting" -ForegroundColor Green
$machinePath = [Environment]::GetEnvironmentVariable("Path", "Machine")
if ($machinePath -notlike "*$InstallDir\cli*") {
    [Environment]::SetEnvironmentVariable("Path", "$machinePath;$InstallDir\cli", "Machine")
}
Start-Service -Name $serviceName

$ready = $false
for ($i = 0; $i -lt 60; $i++) {
    try {
        $response = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/health/ready" -UseBasicParsing -TimeoutSec 2
        if ($response.StatusCode -eq 200) { $ready = $true; break }
    }
    catch {
        Start-Sleep -Seconds 1
    }
}

Write-Host ""
if ($ready) {
    Write-Host "Cabinet is running." -ForegroundColor Green
}
else {
    Write-Host "Cabinet didn't report ready within 60 seconds. Check $InstallDir\api\logs\." -ForegroundColor Yellow
}
Write-Host "  Dashboard: http://localhost:$Port/  (sign in with the admin key)"
Write-Host "  Keys:      $keysPath"
Write-Host "  Settings:  $settingsPath  (rerun this script after editing it)"
Write-Host "  Logs:      $InstallDir\api\logs\"
if ($fingerprint) {
    Write-Host ""
    Write-Host "  Agents send to https://<this server's address>:$HttpsPort and pin this certificate:"
    Write-Host "    CertificateSha256: $fingerprint" -ForegroundColor Cyan
    Write-Host "  (also in $DataDir\https-fingerprint.txt)"
}
Write-Host ""
