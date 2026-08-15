# Phylax — shared delivery core + Defender remediation sketch

## Structure

```
Phylax.Shared/              <- new class library, referenced by Connector AND Remediation
  Models/
    PatchCandidate.cs        one item to patch, regardless of how it was discovered
    DeliveryTarget.cs        describes the destination machine
    DeliveryResult.cs
    VulnerabilityFinding.cs  one Defender CVE-on-a-machine row
  Delivery/
    IPatchDeliveryAdapter.cs
    WsusDeliveryAdapter.cs        fixes the old WsusPublisher stub — actually calls PublishPackage()
    WingetLocalDeliveryAdapter.cs runs winget locally, used INSIDE Phylax.Connector
    WingetArcRunCommandAdapter.cs pushes winget via Azure/Arc Run Command, no agent needed
    DeliveryAdapterResolver.cs    picks an adapter for a target
  Catalog/
    KnownAppCatalog.cs        moved from FunctionApp, namespace fixed (was Phylax.Connector.Services)
    CatalogMatcher.cs         NEW — maps Defender's product/vendor names to winget IDs

Phylax.Remediation/          <- new, separate Function App (timer-triggered)
  Services/
    DefenderVulnerabilityService.cs   pulls findings from Defender for Endpoint API
    RemediationOrchestrator.cs        trigger -> match -> pick adapter -> deliver
  Functions/
    VulnerabilityRemediationTimerFunction.cs   runs every 6h

Phylax.Connector.Additions/  <- patch notes for your EXISTING connector project
  Worker.cs.patch-notes.md   exact diffs to adopt Phylax.Shared instead of local WsusPublisher

scripts/
  install-connector-service.ps1     installs connector as a service, sets Wsus:ServerName correctly
  register-defender-api-app.ps1     Entra app reg + Vulnerability.Read.All consent
```

## How the three tiers you described map to this

**Vanguard (scheduled)** — unchanged in spirit: `Phylax.Connector` runs on a timer,
scans, calls the catalog Function, gets back updates. The only structural change is
that the last mile (WSUS publish, or winget-direct) now goes through
`IPatchDeliveryAdapter` instead of a hardcoded `WsusPublisher` call, so both this path
and the vuln-triggered path share the same delivery code and don't drift into two
implementations of "how do I actually get this update onto a box."

**Winget execution layer** — split into two adapters instead of one, because "target
Arc/IaaS" and "use Vanguard's local winget" are actually different problems:
- `WingetLocalDeliveryAdapter` — lives inside the connector, for boxes that already
  have the agent. No push mechanism needed, it's already there.
- `WingetArcRunCommandAdapter` — lives in the orchestrator/Function side, for boxes
  that *don't* have the agent (or that Defender flagged before you ever deployed
  Vanguard there). Talks to Azure Resource Manager's Run Command API, works for
  both Arc-enabled servers and native Azure IaaS VMs.

**Defender-triggered remediation** — genuinely separate Function App
(`Phylax.Remediation`), because the trigger is different (timer polling Defender's
API, not the connector's own schedule) and the auth is different (Defender API app
registration, not your connector API key). It reuses everything else: same
`PatchCandidate`/`DeliveryTarget` models, same adapters, same `KnownAppCatalog`-style
matching (via the new `CatalogMatcher`, since Defender's product names aren't registry
DisplayNames).

## What's real vs. what needs your hands before it runs

| Piece | Status |
|---|---|
| `WsusDeliveryAdapter` — MSI path | Real logic (PopulatePackageFromWindowsInstaller + PublishPackage + Approve), matches the standard WSUS Publishers API pattern. Untested against your actual WSUS box. |
| `WsusDeliveryAdapter` — EXE path | Flagged inline — I'm not fully certain of the exact `InstallableItem` construction API. Verify against WSUS SDK docs or a working sample before trusting it for Notepad++/Chrome. |
| `WingetLocalDeliveryAdapter` | Straightforward `Process.Start`, should work as-is. Test under whatever account the service runs as — winget's availability to LocalSystem is inconsistent. |
| `WingetArcRunCommandAdapter` | API shape is right, **api-version constants are placeholders** — confirm current versions before use. |
| `DefenderVulnerabilityService` | Endpoint/auth pattern is correct (Defender for Endpoint API via `api.security.microsoft.com`, WindowsDefenderATP app permission), but the response DTO field names are best-effort. Pull one real response from your tenant and correct `DefenderVulnerabilityRow`. |
| `CatalogMatcher` | Placeholder mapping rules — pull real `productName`/`vendor` values from a live Defender query and correct these. This is the file you'll iterate on most. |
| `RemediationOrchestrator` | The `DeliveryTarget` lookup for a given machine is stubbed — needs to hook into your actual device inventory (extend `InventoryStorageService` or query Defender's `/machines/{id}` for AAD device correlation). |

## Suggested order to actually test this

1. Fix WSUS config + install script first (`install-connector-service.ps1`), confirm
   `WsusDeliveryAdapter`'s MSI path publishes successfully against ptg-win25 with 7-Zip
   — that validates the adapter pattern without touching Defender at all.
2. Wire `WingetLocalDeliveryAdapter` into the connector for one of the 2022/2025 test
   boxes, confirm winget upgrades work from the service context.
3. Only then stand up `Phylax.Remediation` — run `register-defender-api-app.ps1`,
   point `DefenderVulnerabilityService` at your tenant, and see what a real response
   looks like before trusting `CatalogMatcher`'s guessed field names.
