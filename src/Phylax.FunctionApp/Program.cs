using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Phylax.FunctionApp.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        // Register Named HttpClient for fetching WinGet manifests safely from GitHub
        services.AddHttpClient("winget", client =>
        {
            client.DefaultRequestHeaders.Add("User-Agent", "Phylax-Vanguard-Engine/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // Register core backend singletons (Replacing Table Storage with Log Analytics Ingestion)
        services.AddSingleton<LogAnalyticsIngestionService>();
        services.AddSingleton<WingetManifestService>();
        services.AddSingleton<VersionComparisonService>();
    })
    .Build();

await host.RunAsync();
