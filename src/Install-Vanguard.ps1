# Install-Vanguard.ps1 - Executed by Azure Arc / VM Extension Handler at Provisioning Time
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

Write-Host "=== Phylax Vanguard Onboarding & Dependency Check ==="
$osBuild = [System.Environment]::OSVersion.Version.Build

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

# 3. Register Phylax.Connector.exe as a Windows Service or Scheduled Task
Write-Host "Registering Phylax Vanguard Execution Service..."
# ... Service installation commands go here ...

Write-Host "=== Vanguard Provisioning Complete ==="
