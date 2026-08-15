# Changes needed in Phylax.Connector to adopt the shared pieces

These aren't full file replacements — just what to change in your existing
`Phylax.Connector` project so it uses `Phylax.Shared` instead of its own
`WsusPublisher`/`PackageBuilder`.

## 1. Add project reference

In `Phylax.Connector.csproj`, add:

```xml
<ItemGroup>
  <ProjectReference Include="..\Phylax.Shared\Phylax.Shared.csproj" />
</ItemGroup>
```

## 2. Program.cs — register the shared adapters instead of WsusPublisher/PackageBuilder

Replace:
```csharp
services.AddSingleton<PackageBuilder>();
services.AddSingleton<WsusPublisher>();
```
with:
```csharp
services.AddHttpClient<Phylax.Shared.Delivery.WsusDeliveryAdapter>();
services.AddSingleton<Phylax.Shared.Delivery.IPatchDeliveryAdapter>(
    sp => sp.GetRequiredService<Phylax.Shared.Delivery.WsusDeliveryAdapter>());
services.AddSingleton<Phylax.Shared.Delivery.WsusDeliveryAdapter>();

services.AddSingleton<Phylax.Shared.Delivery.IPatchDeliveryAdapter,
    Phylax.Shared.Delivery.WingetLocalDeliveryAdapter>();

services.AddSingleton<Phylax.Shared.Delivery.DeliveryAdapterResolver>();
```

You can delete `Services/WsusPublisher.cs` and `Services/PackageBuilder.cs` once
`Worker.cs` is updated — their logic now lives in `WsusDeliveryAdapter`.

## 3. Worker.cs — RunScanCycleAsync

Old flow called `_catalogClient.DownloadPayloadAsync` -> `_packageBuilder.BuildSdpAsync`
-> `_wsusPublisher.Publish` directly. New flow: build a `DeliveryTarget` describing this
box once, then hand each `CatalogUpdate` (converted to `PatchCandidate`) + that target
to the resolver:

```csharp
private readonly DeliveryAdapterResolver _deliveryResolver;

// ...in RunScanCycleAsync, replace the foreach body with:

var target = new DeliveryTarget
{
    MachineName = Environment.MachineName,
    HasVanguardAgent = true,
    WsusManaged = bool.Parse(_configuration["PhylaxConnector:Wsus:Enabled"] ?? "true")
};

foreach (var update in availableUpdates)
{
    var candidate = new PatchCandidate
    {
        ApplicationName = update.ProductName,
        VendorName = update.VendorName,
        WingetId = update.WingetId, // NOTE: CatalogUpdate on the connector side doesn't
                                     // currently carry WingetId — add that field so the
                                     // winget-local adapter has something to call.
        NewVersion = update.NewVersion,
        InstallerUrl = update.DownloadUrl,
        InstallerType = update.InstallerType
    };

    var adapter = _deliveryResolver.Resolve(target);
    var result = adapter is null
        ? DeliveryResult.Fail("none", "No adapter could handle this target")
        : await adapter.DeliverAsync(candidate, target, ct);

    await _catalogClient.ReportStatusAsync(update, result.Success ? "Published" : "Failed", ct);
}
```

This also fixes a latent bug: `CatalogClient.GetAvailableUpdatesAsync` posts to a
**relative** URI (`"api/catalog/updates"`) but `Program.cs` never sets `BaseAddress` on
the injected `HttpClient` — that only worked in your test run because the exe on disk
predates this source (see the version-drift note from the last review). Add to
Program.cs:

```csharp
services.AddHttpClient<CatalogClient>(client =>
{
    client.BaseAddress = new Uri(hostContext.Configuration["PhylaxConnector:ApiBaseUrl"]!);
    client.DefaultRequestHeaders.Add("x-functions-key", hostContext.Configuration["PhylaxConnector:ApiKey"]);
});
```
