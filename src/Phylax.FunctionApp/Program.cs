using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Phylax.FunctionApp.Services;

var host = new HostBuilder()
    // Fixes AZFW0014: Uses the correct ASP.NET Core Integration for .NET isolated workers
    .ConfigureFunctionsWebApplication() 
    .ConfigureServices(services =>
    {
        services.AddSingleton<WingetManifestService>();
        services.AddSingleton<VersionComparisonService>();
        services.AddSingleton<LogAnalyticsIngestionService>();
    })
    .Build();

host.Run();
