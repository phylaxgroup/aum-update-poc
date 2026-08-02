using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Phylax.FunctionApp.Models;
using Phylax.FunctionApp.Services;

namespace Phylax.FunctionApp.Functions;

public class ConnectorStatusFunction
{
    private readonly ILogger<ConnectorStatusFunction> _log;
    private readonly InventoryStorageService _storage;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public ConnectorStatusFunction(
        ILogger<ConnectorStatusFunction> log,
        InventoryStorageService storage)
    {
        _log     = log;
        _storage = storage;
    }

    /// <summary>
    /// POST /api/connector/status
    /// Receives telemetry from the connector service.
    /// Stores it in Table Storage for monitoring and billing metering.
    /// </summary>
    [Function("ConnectorStatus")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post",
            Route = "connector/status")] HttpRequestData req,
        CancellationToken ct)
    {
        ConnectorStatusRequest? status;
        try
        {
            var body = await req.ReadAsStringAsync();
            status   = JsonSerializer.Deserialize<ConnectorStatusRequest>(
                body ?? string.Empty, JsonOptions);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to parse status request");
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid request body");
            return bad;
        }

        if (status is null || string.IsNullOrWhiteSpace(status.TenantId))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("TenantId is required");
            return bad;
        }

        _log.LogInformation(
            "Connector status received: Tenant={TenantId} | " +
            "Machine={Machine} | Inventory={Inventory} | Updates={Updates} | " +
            "Version={Version}",
            status.TenantId, status.MachineName,
            status.InventoryCount, status.UpdatesFound,
            status.ConnectorVersion);

        await _storage.StoreStatusAsync(status, ct);

        var ok = req.CreateResponse(HttpStatusCode.OK);
        await ok.WriteStringAsync("OK");
        return ok;
    }
}