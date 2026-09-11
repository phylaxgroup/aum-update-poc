# Phylax Vanguard — architecture

Third-party application patching for Windows Server estates, delivered through WSUS so that
updates appear alongside Microsoft patches in Azure Update Manager's assessment and remediation.

## Projects

```
Phylax.Connector/            net10.0-windows — the Vanguard agent
  Services/
    InventoryScanner.cs        HKLM Uninstall-hive scan, noise filtering, WingetId resolution
    CatalogClient.cs           POSTs inventory to the catalog Function, gets back needed updates
    LocalInstallerService.cs   winget delivery mode; requires a resolved WingetId
  Worker.cs                    BackgroundService; 6h cycle; dispatches on DeliveryMode

Phylax.WsusPublisher/        net48 — standalone console exe, invoked by the connector
  WsusPackagePublisher.cs      download+verify -> build SDP -> publish to WSUS -> approve
  Program.cs                   argument parsing; exit codes are the connector's contract

Phylax.FunctionApp/          net10.0 — the catalog service (Azure Function App)
  Functions/
    CatalogUpdatesFunction.cs  matches submitted inventory against MasterCatalog
    ConnectorStatusFunction.cs
    ConnectorInventoryFunction.cs
  Inventory/
    InventoryStore.cs          Azure Table upsert of a device row per connector call-in
  Services/
    WingetManifestService.cs   live winget manifest lookup for catalog drift detection
    VersionComparisonService.cs

Phylax.Shared/               net48;net10.0-windows — KnownAppCatalog only
  Catalog/KnownAppCatalog.cs   DisplayName/Publisher -> WinGet package ID mappings

scripts/
  install-connector-service.ps1   installs the connector as a service; requires -WsusServerName
```

## Flow

1. `Worker` wakes (6h interval) and resolves `DeliveryMode` from config: `wsus`, `winget`, or
   `inventory-only`. In `wsus` mode it fail-fast checks that `Phylax.WsusPublisher.exe` exists at
   `PhylaxConnector:WsusPublisherPath`, and **silently downgrades to `inventory-only` if not** —
   check the startup log line for the mode it actually resolved.
2. `InventoryScanner` enumerates both HKLM `Uninstall` hives, filters OS/driver/runtime noise by
   publisher and name fragment, collapses duplicate ARP entries for the same product, and resolves
   a `WingetId` per app via `KnownAppCatalog` first, then a fuzzy `winget search` CLI fallback.
3. `CatalogClient` POSTs the inventory to `CatalogUpdatesFunction`, which matches each entry
   against `MasterCatalog` by `DisplayName.Contains(ApplicationName)` and returns those whose
   installed version trails the pinned `LatestVersion`. It also logs a warning when winget's live
   manifest is ahead of the pinned entry (catalog drift) without changing what is delivered.
4. In `wsus` mode the connector shells out to `Phylax.WsusPublisher.exe` per update. That binary
   downloads and SHA256-verifies the installer, builds an SDP, rewrites
   `ApplicationSpecificData` into `UpdateSpecificData` so the package classifies as a Security
   Update rather than an application install, publishes it, and optionally approves it.
5. WSUS clients detect the update on their next scan; Azure Update Manager surfaces it for
   assessment and can install it.

## Why the publisher is a separate net48 process

`Microsoft.UpdateServices.Administration` is a .NET Framework assembly and cannot be loaded from
modern .NET — verified 2026-08-16: identical probe code connects on net48 and throws
`FileNotFoundException` on net8.0-windows (presumed to still hold on net10.0-windows, not
independently re-verified). `Phylax.WsusPublisher` exists solely to cross that boundary, which is
why it has a deliberately minimal dependency surface and communicates via exit codes. Do not
"simplify" it back into the connector.

It is also the only project referencing that assembly, so it is the only one requiring WSUS or the
RSAT WSUS tools on the build machine. Everything else builds anywhere.

## Operational constraints

**Publish from one machine.** WSUS publishing is a server-wide action. Run the connector in `wsus`
mode on a single designated host — ideally the WSUS server itself, where the Administration API's
dependency chain is already correctly GAC-registered.

**`WsusPublisherPath` is machine-specific.** An `appsettings.json` copied between hosts will point
at a path that exists on only one of them, and the connector will quietly fall back to
`inventory-only` on the others.

**Applicability is the SDP's, not ours.** `PopulatePackageFromWindowsInstaller` derives detection
rules from the installer. A machine already carrying the target version is correctly reported as
not needing the update.

**Cross-provenance patching leaves ARP inconsistent.** An MSI major upgrade only supersedes MSI
products sharing its UpgradeCode, so patching an EXE-installed app with an MSI leaves the original
Uninstall entry behind even when the binaries are overwritten in place. `InventoryScanner`
collapses these; the orphaned entry and its stale uninstaller remain on disk. Do not build
remove-then-install remediation on top of a captured `UninstallString` without accounting for this.

## Known gaps

- `MasterCatalog` is a hardcoded list matched by display-name substring. The `WingetId` the scanner
  resolves plays no part in the match decision. This does not scale to an arbitrary customer estate
  and is the most significant open design question.
- `InjectSecuritySchemaAndKb` hardcodes `MsrcSeverity="Critical"` for every package.
- `appsettings.json` carries an API key and tenant ID in source control and is not gitignored.
- `KnownAppCatalog`'s `^Git(\s+version)?` rule also matches `GitHub Desktop`.

## History

A second generation targeting Defender for Endpoint vulnerability findings — `Phylax.Remediation`,
the `IPatchDeliveryAdapter` abstraction and its adapters, `CatalogMatcher`, and the
`PatchCandidate`/`DeliveryTarget` models — was explored and removed on 2026-09-11 as an abandoned
side track. It never ran against a live tenant. Its WSUS adapter used the in-process assembly load
pattern that the connector had already abandoned, so it would likely have failed at
`AdminProxy.GetUpdateServer()` had it been exercised.
