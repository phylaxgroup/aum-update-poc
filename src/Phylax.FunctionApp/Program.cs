using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Phylax.FunctionApp.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
        
        services.AddSingleton<WingetManifestService>();
        services.AddSingleton<VersionComparisonService>();
        services.AddSingleton<LogAnalyticsIngestionService>();
    })
    .Build();

host.Run();
