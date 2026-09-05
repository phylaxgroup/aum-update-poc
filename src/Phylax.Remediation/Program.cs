using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Phylax.Remediation.Services;
using Phylax.Shared.Delivery;
using Phylax.Shared.Inventory;

var builder = FunctionsApplication.CreateBuilder(args);


var config = builder.Configuration;

// Same "PhylaxDeviceInventory" table Phylax.FunctionApp writes to on every connector call-in -
// this is what lets RemediationOrchestrator resolve a real DeliveryTarget instead of guessing.
builder.Services.AddSingleton(_ => new InventoryStore(
    Environment.GetEnvironmentVariable("AzureWebJobsStorage")
        ?? throw new InvalidOperationException(
            "AzureWebJobsStorage is required to initialize the device inventory store.")));

// Defender for Endpoint app registration credentials — see scripts/register-defender-api-app.ps1.
// ClientSecretCredential shown for POC speed; swap to a certificate or managed identity
// (with federated credential on the app registration) before this leaves the test tenant.
builder.Services.AddSingleton<TokenCredential>(_ => new ClientSecretCredential(
    tenantId: config["Defender:TenantId"],
    clientId: config["Defender:ClientId"],
    clientSecret: config["Defender:ClientSecret"]));

builder.Services.AddHttpClient<DefenderVulnerabilityService>();
builder.Services.AddHttpClient<WingetArcRunCommandAdapter>();
builder.Services.AddHttpClient<WsusDeliveryAdapter>();

// Registration ORDER matters — DeliveryAdapterResolver picks the first adapter whose
// CanHandle() returns true. WSUS first so WSUS-managed boxes stay on WSUS.
builder.Services.AddSingleton<IPatchDeliveryAdapter>(sp => sp.GetRequiredService<WsusDeliveryAdapter>());
builder.Services.AddSingleton<WsusDeliveryAdapter>();
builder.Services.AddSingleton<IPatchDeliveryAdapter>(sp => sp.GetRequiredService<WingetArcRunCommandAdapter>());
builder.Services.AddSingleton<WingetArcRunCommandAdapter>();

builder.Services.AddSingleton<DeliveryAdapterResolver>();
builder.Services.AddSingleton<RemediationOrchestrator>();

builder.Build().Run();
