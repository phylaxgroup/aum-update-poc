using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Phylax.FunctionApp.Models;
using Phylax.FunctionApp.Services;

namespace Phylax.FunctionApp.Functions;

public class ConnectorInventoryFunction
{
    private readonly ILogger<ConnectorInventoryFunction> _log;
    private readonly LogAnalyticsIngestionService _ingestionService;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public ConnectorInventoryFunction(
        ILogger<ConnectorInventoryFunction> log,
        LogAnalyticsIngestionService ingestionService)
    {
        _log              = log;
        _ingestionService = ingestionService;
    }

    [Function("ConnectorInventory")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post",
            Route = "connector/inventory")] HttpRequestData req,
        CancellationToken ct)
    {
        var tenantId    = req.Headers.GetValues("X-Tenant-Id").FirstOrDefault();
        var machineName = req.Headers.GetValues("X-Machine-Name").FirstOrDefault();

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("X-Tenant-Id header is required");
            return bad;
        }

        List<AppInventoryItem>? apps;
        try
        {
            var body = await req.ReadAsStringAsync();
            apps     = JsonSerializer.Deserialize<List<AppInventoryItem>>(
                body ?? string.Empty, JsonOptions);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to parse inventory request");
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid request body");
            return bad;
        }

        if (apps is null || apps.Count == 0)
        {
            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteStringAsync("No apps in payload");
            return ok;
        }

        // Stream snapshot directly to Log Analytics
        await _ingestionService.UploadInventoryAsync(
            tenantId,
            machineName ?? "unknown",
            apps,
            ct);

        _log.LogInformation(
            "Ingested {Count} inventory records for tenant {Tenant} machine {Machine} into Log Analytics",
            apps.Count, tenantId, machineName);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteStringAsync($"Ingested {apps.Count} inventory records to workspace");
        return response;
    }
}
