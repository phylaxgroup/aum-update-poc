<#
.SYNOPSIS
    Phylax Vanguard - Enterprise Endpoint Onboarding Script
.DESCRIPTION
    Bootstraps the local OS with the required WinGet engine, downloads the latest 
    Phylax.Connector payload from Azure Blob Storage, and registers it as a native 
    Windows Service running under NT AUTHORITY\SYSTEM.
#>

# Requires Run as Administrator
if (!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Warning "This script must be run as an Administrator."
    break
}

Write-Host "--- Starting Phylax Vanguard Deployment ---" -ForegroundColor Cyan

# ============================================================================
# CONFIGURATION - PASTE YOUR AZURE BLOB SAS URLS HERE
# ============================================================================
$exeSasUrl  = "https://phylaxdeployst.blob.core.windows.net/vanguard-releases/Phylax.Connector.exe?sp=r&st=2026-08-09T17:49:17Z&se=2026-08-10T02:04:17Z&spr=https&sv=2026-02-06&sr=c&sig=Ius539O8f5YukTmpDIOR8NlSq5XzBMr8TNQ%2Fv97TX%2Fo%3D"
$jsonSasUrl = "https://phylaxdeployst.blob.core.windows.net/vanguard-releases/appsettings.json?sp=r&st=2026-08-09T17:49:17Z&se=2026-08-10T02:04:17Z&spr=https&sv=2026-02-06&sr=c&sig=Ius539O8f5YukTmpDIOR8NlSq5XzBMr8TNQ%2Fv97TX%2Fo%3D"

$installDir = "C:\Program Files\Phylax Vanguard"
$serviceName = "PhylaxVanguard"
$serviceBinaryPath = "$installDir\Phylax.Connector.exe"

# ============================================================================
# PHASE 1: OS BOOTSTRAP (WinGet v1.8 Injection)
# ============================================================================
Write-Host "[1/4] Validating local OS prerequisites..."

# Grab the OS Build number to evaluate compatibility
$osBuild = [int](Get-CimInstance Win32_OperatingSystem).BuildNumber

# 1. OS Compatibility Check
if ($osBuild -lt 17763) {
    Write-Warning "Windows Server 2016 (Build $osBuild) detected. WinGet is unsupported on this OS."
    exit 0
}

# 2. Check if WinGet is already installed system-wide
$wingetPath = (Get-Command winget -ErrorAction SilentlyContinue).Source
if (-not $wingetPath) {
    Write-Host "WinGet engine not found. Bootstrapping Stable WinGet v1.8 System-Wide..." -ForegroundColor Cyan
    
    $workDir = "$env:TEMP\VanguardBootstrap"
    New-Item -ItemType Directory -Path $workDir -Force | Out-Null
    
    try {
        Write-Host "Downloading WinGet v1.8 Bundle & Enterprise License XML..."
        Invoke-WebRequest -Uri "https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx" -OutFile "$workDir\VCLibs.appx"
        Invoke-WebRequest -Uri "https://github.com/microsoft/microsoft-ui-xaml/releases/download/v2.8.6/Microsoft.UI.Xaml.2.8.x64.appx" -OutFile "$workDir\UIXaml.appx"
        Invoke-WebRequest -Uri "https://github.com/microsoft/winget-cli/releases/download/v1.8.1911/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle" -OutFile "$workDir\WinGet.msixbundle"
        Invoke-WebRequest -Uri "https://github.com/microsoft/winget-cli/releases/download/v1.8.1911/76fba573f02545629706ab99170237bc_License1.xml" -OutFile "$workDir\License1.xml"

        Write-Host "Provisioning WinGet System-Wide via DISM..."
        Add-AppxProvisionedPackage -Online -PackagePath "$workDir\WinGet.msixbundle" -LicensePath "$workDir\License1.xml" -DependencyPackagePath "$workDir\VCLibs.appx","$workDir\UIXaml.appx"
        
        Write-Host "WinGet v1.8 globally provisioned!" -ForegroundColor Green
    }
    finally {
        Remove-Item -Path $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }
} else {
    Write-Host "WinGet engine already present at: $wingetPath" -ForegroundColor Green
}

Write-Host "OS Bootstrap complete." -ForegroundColor Green


# ============================================================================
# PHASE 2: DIRECTORY PREPARATION
# ============================================================================
Write-Host "[2/4] Preparing secure deployment directories..."

if (!(Test-Path -Path $installDir)) {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
    Write-Host "Created directory: $installDir"
} else {
    Write-Host "Target directory already exists."
}


# ============================================================================
# PHASE 3: PAYLOAD DELIVERY (Azure Blob Storage)
# ============================================================================
Write-Host "[3/4] Downloading latest Vanguard engine from Azure..."

try {
    # Download the executable
    Write-Host " -> Downloading Phylax.Connector.exe..."
    Invoke-WebRequest -Uri $exeSasUrl -OutFile "$installDir\Phylax.Connector.exe" -UseBasicParsing -ErrorAction Stop
    
    # Download the configuration
    Write-Host " -> Downloading appsettings.json..."
    Invoke-WebRequest -Uri $jsonSasUrl -OutFile "$installDir\appsettings.json" -UseBasicParsing -ErrorAction Stop
    
    Write-Host "Payload delivery successful." -ForegroundColor Green
}
catch {
    Write-Error "Failed to download payload from Azure Blob Storage. Verify SAS tokens are active and not expired."
    Write-Error $_.Exception.Message
    exit 1
}


# ============================================================================
# PHASE 4: SERVICE REGISTRATION & EXECUTION
# ============================================================================
Write-Host "[4/4] Registering Vanguard Windows Service..."

# Stop and delete the service if it already exists (allows for seamless upgrades)
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Write-Host "Existing Vanguard service detected. Stopping and removing..."
    Stop-Service -Name $serviceName -Force
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 3 # Allow SCM time to clear the lock
}

# Register the .NET Worker as a native SYSTEM service
Write-Host "Registering service: $serviceName..."
New-Service -Name $serviceName `
            -BinaryPathName $serviceBinaryPath `
            -DisplayName "Phylax Vanguard Update Connector" `
            -Description "Desired-state application patching engine linked to Azure Update Manager." `
            -StartupType Automatic | Out-Null

# Start the engine
Write-Host "Starting Vanguard service..."
Start-Service -Name $serviceName

# Verify running state
$svcStatus = (Get-Service -Name $serviceName).Status
if ($svcStatus -eq 'Running') {
    Write-Host "--- Phylax Vanguard Deployment Complete! ---" -ForegroundColor Green
    Write-Host "The engine is now running in the background as NT AUTHORITY\SYSTEM." -ForegroundColor Green
} else {
    Write-Warning "Service was registered but failed to start. Check the Windows Event Viewer for .NET runtime errors."
}
