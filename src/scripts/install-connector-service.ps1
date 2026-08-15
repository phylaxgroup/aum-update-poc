#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs (or reconfigures) the Phylax Connector as a Windows Service and sets the
    WSUS server target explicitly  -  fixes the config gap that caused
    WsusInvalidServerException when the connector ran on a client box with no
    PhylaxConnector:Wsus:ServerName set (defaulted to "localhost").

.PARAMETER WsusServerName
    The actual WSUS server hostname, e.g. ptg-win25. NOT the box this script runs on
    unless this IS the WSUS box.

.EXAMPLE
    .\install-connector-service.ps1 -WsusServerName ptg-win25 -WsusPort 8530
#>
param(
    [string]$InstallPath = "C:\Program Files\Phylax Vanguard",
    [Parameter(Mandatory = $true)][string]$WsusServerName,
    [int]$WsusPort = 8530,
    [bool]$WsusUseSsl = $false,
    [string]$WsusTargetGroup = "All Computers",
    [bool]$WsusAutoApprove = $false,
    [string]$ServiceName = "PhylaxVanguardConnector"
)

$ErrorActionPreference = "Stop"

$exePath = Join-Path $InstallPath "Phylax.Connector.exe"
if (-not (Test-Path $exePath)) {
    throw "Phylax.Connector.exe not found at $exePath  -  copy the build there first."
}

# --- Patch appsettings.json with the WSUS target so it's never relying on defaults ---
$settingsPath = Join-Path $InstallPath "appsettings.json"
$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json

if (-not $settings.PhylaxConnector.PSObject.Properties.Match("Wsus").Count) {
    $settings.PhylaxConnector | Add-Member -NotePropertyName "Wsus" -NotePropertyValue ([PSCustomObject]@{})
}
$settings.PhylaxConnector.Wsus = [PSCustomObject]@{
    ServerName   = $WsusServerName
    Port         = $WsusPort
    UseSsl       = $WsusUseSsl
    Enabled      = $true
    TargetGroupName = $WsusTargetGroup
    AutoApprove  = $WsusAutoApprove
}
$settings | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding UTF8
Write-Host "Updated $settingsPath with Wsus:ServerName = $WsusServerName" -ForegroundColor Green

# --- Sanity check: is this box actually able to reach WSUS admin API at all? ---
$wsusToolsPath = "C:\Program Files\Update Services\Api\Microsoft.UpdateServices.Administration.dll"
if (-not (Test-Path $wsusToolsPath)) {
    Write-Warning @"
WSUS Administration API assembly not found at $wsusToolsPath on this machine.
Remote WSUS calls typically require the WSUS Administration Console (RSAT: WSUS Tools,
or the "Windows Server Update Services" management console feature) to be installed
locally, even when the WSUS server itself is remote. Install it via:
    Install-WindowsFeature -Name UpdateServices-UI  (on Windows Server)
or the equivalent RSAT capability on a client OS before expecting the connector to
connect successfully.
"@
}

# --- Ensure the account this runs as can actually reach the WSUS server ---
Write-Host "Testing TCP connectivity to $WsusServerName`:$WsusPort ..." -ForegroundColor Cyan
$test = Test-NetConnection -ComputerName $WsusServerName -Port $WsusPort -WarningAction SilentlyContinue
if (-not $test.TcpTestSucceeded) {
    Write-Warning "Could not reach $WsusServerName on port $WsusPort from this machine. Check firewall rules between here and the WSUS server before troubleshooting the app further."
}

# --- Install or update the Windows Service ---
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service $ServiceName already exists  -  stopping to apply config." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
} else {
    New-Service -Name $ServiceName `
        -BinaryPathName "`"$exePath`"" `
        -DisplayName "Phylax Vanguard Connector" `
        -Description "Scans installed software and publishes third-party updates to WSUS / winget." `
        -StartupType Automatic
    Write-Host "Created service $ServiceName" -ForegroundColor Green
}

# Restart on failure  -  this is a background patching agent, don't let one bad WSUS call
# (like the ones in your log) leave it dead until next login.
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

Start-Service -Name $ServiceName
Write-Host "Service $ServiceName started. Tail logs with:" -ForegroundColor Green
Write-Host "  Get-EventLog -LogName Application -Source $ServiceName -Newest 20" -ForegroundColor DarkGray
