using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Phylax.FunctionApp.Models;
using Phylax.FunctionApp.Services;

namespace Phylax.FunctionApp.Functions;

public class CatalogUpdatesFunction
{
    private readonly ILogger<CatalogUpdatesFunction> _logger;
    private readonly LogAnalyticsIngestionService _ingestionService;
    private readonly VersionComparisonService _versionComparison;

    public CatalogUpdatesFunction(
        ILogger<CatalogUpdatesFunction> logger,
        LogAnalyticsIngestionService ingestionService,
        VersionComparisonService versionComparison)
    {
        _logger = logger;
        _ingestionService = ingestionService;
        _versionComparison = versionComparison;
    }

    [Function("CatalogUpdates")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "catalog/updates")] HttpRequest req)
    {
        _logger.LogInformation("CatalogUpdates HTTP trigger endpoint invoked.");

        string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(requestBody))
        {
            return new BadRequestObjectResult("Request body cannot be empty.");
        }

        var updateRequest = JsonSerializer.Deserialize<CatalogUpdateRequest>(requestBody,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        if (updateRequest == null || updateRequest.Apps == null)
        {
            return new BadRequestObjectResult("Invalid payload structure.");
        }

        string machineName = req.Headers["x-machine-name"].ToString();
        if (string.IsNullOrWhiteSpace(machineName))
        {
            machineName = "Unknown-Vanguard-Host";
        }

        // 1. Asynchronously push telemetry to Log Analytics Workspace via DCR
        await _ingestionService.UploadInventoryAsync(
            updateRequest.TenantId,
            machineName,
            updateRequest.Apps,
            req.HttpContext.RequestAborted);

        // 2. Perform version comparison using your Winget manifest engine
        var availableUpdates = await _versionComparison.GetAvailableUpdatesAsync(
            updateRequest.Apps,
            req.HttpContext.RequestAborted);

        // 3. Formulate the response payload for the Vanguard endpoint
        var response = new CatalogUpdateResponse
        {
            TenantId = updateRequest.TenantId,
            GeneratedAt = DateTimeOffset.UtcNow,
            UpdatesAvailable = availableUpdates.Count,
            Updates = availableUpdates
        };

        return new OkObjectResult(response);
    }
}
