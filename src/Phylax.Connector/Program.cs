using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Phylax.Connector;
using Phylax.Connector.Configuration;
using Phylax.Connector.Services;

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "PhylaxConnector";
    })
    .ConfigureServices((context, services) =>
    {
        // Bind configuration sections
        var configSection = context.Configuration.GetSection("PhylaxConnector");
        services.Configure<PhylaxConnectorOptions>(configSection);
        var options = configSection.Get<PhylaxConnectorOptions>();

        // Register the Named HTTP Client for the Function App backend
        services.AddHttpClient<CatalogClient>(client =>
        {
            client.BaseAddress = new Uri(options?.ApiBaseUrl ?? "http://localhost");
            if (!string.IsNullOrWhiteSpace(options?.ApiKey))
            {
                client.DefaultRequestHeaders.Add("x-functions-key", options.ApiKey);
            }
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        // Register clean engine singletons
        services.AddSingleton<InventoryScanner>();
        services.AddSingleton<LocalInstallerService>();

        // NOTE: WSUS publishing is no longer done in-process. Microsoft.UpdateServices.Administration
        // is a .NET Framework assembly that cannot be loaded from modern .NET (originally hit on
        // net8.0-windows, presumed to still apply now that this project targets net10.0-windows -
        // it's a .NET Framework-vs-modern-.NET loading issue, not specific to any one modern TFM),
        // so it now lives in the standalone net48 Phylax.WsusPublisher.exe, which this connector
        // invokes as a child process when DeliveryMode is 'wsus'.

        // Main execution background loop
        services.AddHostedService<Worker>();
    })
    .Build();

await host.RunAsync();