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
    private readonly InventoryStorageService _storage;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public ConnectorInventoryFunction(
        ILogger<ConnectorInventoryFunction> log,
        InventoryStorageService storage)
    {
        _log     = log;
        _storage = storage;
    }

    /// <summary>
    /// POST /api/connector/inventory
    /// Stores full inventory snapshot from a connector instance.
    /// Used for fleet-wide software visibility in the Phylax dashboard.
    /// </summary>
    [Function("ConnectorInventory")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post",
            Route = "connector/inventory")] HttpRequestData req,
        CancellationToken ct)
    {
        var tenantId    = req.Headers
            .GetValues("X-Tenant-Id")
            .FirstOrDefault();
        var machineName = req.Headers
            .GetValues("X-Machine-Name")
            .FirstOrDefault();

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

        await _storage.UpsertInventoryAsync(
            tenantId,
            machineName ?? "unknown",
            apps,
            ct);

        _log.LogInformation(
            "Stored {Count} inventory records for " +
            "tenant {Tenant} machine {Machine}",
            apps.Count, tenantId, machineName);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteStringAsync(
            $"Stored {apps.Count} inventory records");
        return response;
    }
}