using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Phylax.FunctionApp.Models;
using Phylax.FunctionApp.Services;

namespace Phylax.FunctionApp.Functions;

public class ConnectorInventoryFunction
{
    private readonly ILogger<ConnectorInventoryFunction> _logger;
    private readonly InventoryStorageService _storageService;
    private readonly LogAnalyticsIngestionService _ingestionService;

    public ConnectorInventoryFunction(
        ILogger<ConnectorInventoryFunction> logger,
        InventoryStorageService storageService,
        LogAnalyticsIngestionService ingestionService)
    {
        _logger = logger;
        _storageService = storageService;
        _ingestionService = ingestionService;
    }

    [Function("ConnectorInventory")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "connector/inventory")] HttpRequestData req)
    {
        _logger.LogInformation("Receiving machine inventory payload.");

        var requestData = await req.ReadFromJsonAsync<CatalogUpdateRequest>();
        if (requestData is null)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        // 1. Persist the inventory list to Azure Table Storage
        await _storageService.SaveInventoryAsync(requestData.MachineName, requestData.InstalledApplications);

        // 2. Stream to Log Analytics Workspace via the updated ingestion helper
        await _ingestionService.ProcessIngestionAsync(requestData.MachineName, requestData.InstalledApplications);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteStringAsync("Inventory logged successfully.");
        return response;
    }
}
