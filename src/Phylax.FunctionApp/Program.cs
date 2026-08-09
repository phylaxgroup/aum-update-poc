using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Phylax.FunctionApp.Services;

var builder = FunctionsApplication.CreateBuilder(args);

// Satisfies the ASP.NET Core integration requirements for extension packages
builder.ConfigureFunctionsWebApplication();

// Default HttpClient
builder.Services.AddHttpClient();

// Named client for winget GitHub API calls
builder.Services.AddHttpClient("winget", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("PhylaxFunctionApp/1.0");
    client.DefaultRequestHeaders.Accept.ParseAdd(
        "application/vnd.github.v3+json");
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Core backend services
builder.Services.AddSingleton<WingetManifestService>();
builder.Services.AddSingleton<VersionComparisonService>();
builder.Services.AddSingleton<LogAnalyticsIngestionService>(); // Replaced InventoryStorageService

builder.Build().Run();
