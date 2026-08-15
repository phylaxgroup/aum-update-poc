#Requires -Modules Microsoft.Graph.Applications, Microsoft.Graph.Authentication
<#
.SYNOPSIS
    Registers an Entra ID app registration with application-level access to the
    Defender for Endpoint API (Vulnerability.Read.All), for use by
    Phylax.Remediation's DefenderVulnerabilityService.

.NOTES
    - Must be run by (or followed by) a Global Administrator / Privileged Role
      Administrator — application permission grants require admin consent, and this
      script will pause and tell you exactly where to click if it can't consent
      programmatically in your tenant.
    - The Defender for Endpoint API's app ID is fixed across all tenants:
        fc780465-2017-40d4-a0c5-307022471b92  ("WindowsDefenderATP")
      This is a Microsoft first-party API, not something you create.
#>
param(
    [string]$AppDisplayName = "Phylax Remediation - Defender API",
    [string]$CertOrSecretDescription = "phylax-remediation-poc"
)

$ErrorActionPreference = "Stop"

Connect-MgGraph -Scopes "Application.ReadWrite.All", "AppRoleAssignment.ReadWrite.All"

# Defender for Endpoint's well-known first-party API app + the app role we need.
$defenderApiAppId = "fc780465-2017-40d4-a0c5-307022471b92"
$defenderSp = Get-MgServicePrincipal -Filter "appId eq '$defenderApiAppId'"
if (-not $defenderSp) {
    throw "Could not find the WindowsDefenderATP service principal in this tenant — is Defender for Endpoint licensed/enabled?"
}

$vulnReadRole = $defenderSp.AppRoles | Where-Object { $_.Value -eq "Vulnerability.Read.All" -and $_.AllowedMemberTypes -contains "Application" }
if (-not $vulnReadRole) {
    throw "Vulnerability.Read.All application role not found on the WindowsDefenderATP service principal. Check current role names at learn.microsoft.com/defender-endpoint/api/exposed-apis-create-app-webapp."
}

# --- Create the app registration ---
$app = New-MgApplication -DisplayName $AppDisplayName -SignInAudience "AzureADMyOrg"
$sp = New-MgServicePrincipal -AppId $app.AppId

Write-Host "Created app '$AppDisplayName' (AppId: $($app.AppId))" -ForegroundColor Green

# --- Request the Vulnerability.Read.All application permission ---
Update-MgApplication -ApplicationId $app.Id -RequiredResourceAccess @(
    @{
        ResourceAppId  = $defenderApiAppId
        ResourceAccess = @(
            @{ Id = $vulnReadRole.Id; Type = "Role" }
        )
    }
)

# --- Attempt admin consent (application permission grant) ---
try {
    New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $sp.Id -BodyParameter @{
        principalId = $sp.Id
        resourceId  = $defenderSp.Id
        appRoleId   = $vulnReadRole.Id
    } | Out-Null
    Write-Host "Admin consent granted for Vulnerability.Read.All." -ForegroundColor Green
}
catch {
    Write-Warning @"
Could not grant consent programmatically (this account may not have sufficient
privilege, or conditional access is blocking it). Grant it manually:
  Entra admin center > App registrations > $AppDisplayName > API permissions >
  Add a permission > APIs my organization uses > WindowsDefenderATP >
  Application permissions > Vulnerability.Read.All > Add permissions >
  Grant admin consent for <tenant>
"@
}

# --- Create a client secret (swap for a certificate before production use) ---
$secret = Add-MgApplicationPassword -ApplicationId $app.Id -PasswordCredential @{
    displayName = $CertOrSecretDescription
    endDateTime = (Get-Date).AddMonths(6)
}

Write-Host "`n--- Save these into Phylax.Remediation local.settings.json / Function App config ---" -ForegroundColor Cyan
Write-Host "Defender:TenantId     = $((Get-MgContext).TenantId)"
Write-Host "Defender:ClientId     = $($app.AppId)"
Write-Host "Defender:ClientSecret = $($secret.SecretText)"
Write-Host "`n(secret shown once — Graph won't return it again; store it in Key Vault, not local.settings.json, once past POC)" -ForegroundColor Yellow
