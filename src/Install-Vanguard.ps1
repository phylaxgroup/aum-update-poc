# Install-Vanguard.ps1 - Executed by Azure Arc / VM Extension Handler at Provisioning Time
$ErrorActionPreference = "Stop"

Write-Host "=== Phylax Vanguard Onboarding & Dependency Check ==="
$osBuild = [System.Environment]::OSVersion.Version.Build

# 1. OS Compatibility Check
if ($osBuild -lt 17763) {
    Write-Warning "Windows Server 2016 (Build $osBuild) detected. WinGet is unsupported on this OS."
    # Here you can either exit with a specific code or configure Vanguard's appsettings.json for a legacy fallback engine
    exit 0
}

# 2. Check if WinGet is already installed and functional
$wingetPath = (Get-Command winget -ErrorAction SilentlyContinue).Source
if (-not $wingetPath) {
    Write-Host "WinGet engine not found. Bootstrapping Microsoft Desktop App Installer..." -ForegroundColor Cyan
    
    $workDir = "$env:TEMP\VanguardBootstrap"
    New-Item -ItemType Directory -Path $workDir -Force | Out-Null
    
    try {
        Invoke-WebRequest -Uri "https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx" -OutFile "$workDir\VCLibs.appx"
        Invoke-WebRequest -Uri "https://github.com/microsoft/microsoft-ui-xaml/releases/download/v2.8.6/Microsoft.UI.Xaml.2.8.x64.appx" -OutFile "$workDir\UIXaml.appx"
        Invoke-WebRequest -Uri "https://aka.ms/getwinget" -OutFile "$workDir\WinGet.msixbundle"

        Add-AppxPackage -Path "$workDir\VCLibs.appx"
        Add-AppxPackage -Path "$workDir\UIXaml.appx"
        Add-AppxPackage -Path "$workDir\WinGet.msixbundle"
        Write-Host "WinGet dependencies successfully provisioned." -ForegroundColor Green
    }
    finally {
        Remove-Item -Path $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }
} else {
    Write-Host "WinGet engine already present at: $wingetPath" -ForegroundColor Green
}

# 3. Register Phylax.Connector.exe as a Windows Service or Scheduled Task
Write-Host "Registering Phylax Vanguard Execution Service..."
# ... Service installation commands go here ...

Write-Host "=== Vanguard Provisioning Complete ==="
