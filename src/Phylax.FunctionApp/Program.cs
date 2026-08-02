using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Phylax.FunctionApp.Services;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Default HttpClient
builder.Services.AddHttpClient();

// Named client for winget GitHub API calls
// GitHub requires a User-Agent header — requests without it return 403
builder.Services.AddHttpClient("winget", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("PhylaxFunctionApp/1.0");
    client.DefaultRequestHeaders.Accept.ParseAdd(
        "application/vnd.github.v3+json");
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Core services
builder.Services.AddSingleton<WingetManifestService>();
builder.Services.AddSingleton<VersionComparisonService>();
builder.Services.AddSingleton<InventoryStorageService>();

builder.Build().Run();