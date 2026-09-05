using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Phylax.FunctionApp.Inventory;
using Phylax.FunctionApp.Services;

var host = new HostBuilder()
    // Fixes AZFW0014: Uses the correct ASP.NET Core Integration for .NET isolated workers
    .ConfigureFunctionsWebApplication() 
    .ConfigureServices(services =>
    {
        services.AddMemoryCache();

        // GitHub's API requires a User-Agent header on every request or it 403s.
        // Unauthenticated GitHub API calls are rate-limited to 60/hr per caller IP - the
        // IMemoryCache in WingetManifestService keeps this well under that across a fleet.
        services.AddHttpClient("winget", client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Phylax-Catalog/1.0");
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        });

        services.AddSingleton<WingetManifestService>();
        services.AddSingleton<VersionComparisonService>();
        services.AddSingleton<LogAnalyticsIngestionService>();
        services.AddSingleton(_ => new InventoryStore(
            Environment.GetEnvironmentVariable("AzureWebJobsStorage")
                ?? throw new InvalidOperationException(
                    "AzureWebJobsStorage is required to initialize the device inventory store.")));
    })
    .Build();

host.Run();
