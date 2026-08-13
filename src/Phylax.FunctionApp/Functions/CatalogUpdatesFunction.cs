using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Phylax.FunctionApp.Models;
using Phylax.FunctionApp.Services;

namespace Phylax.FunctionApp.Functions;

public class CatalogUpdatesFunction
{
    private readonly ILogger<CatalogUpdatesFunction> _log;
    private readonly VersionComparisonService _versionService;
    private readonly InventoryStorageService _storage;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public CatalogUpdatesFunction(
        ILogger<CatalogUpdatesFunction> log,
        VersionComparisonService versionService,
        InventoryStorageService storage)
    {
        _log            = log;
        _versionService = versionService;
        _storage        = storage;
    }

    [Function("CatalogUpdates")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post",
            Route = "catalog/updates")] HttpRequestData req,
        CancellationToken ct)
    {
        _log.LogInformation("CatalogUpdates triggered");

        // Parse request body
        CatalogUpdateRequest? request;
        try
        {
            var body = await req.ReadAsStringAsync();
            request  = JsonSerializer.Deserialize<CatalogUpdateRequest>(
                body ?? string.Empty, JsonOptions);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to parse request body");
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid request body");
            return badRequest;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.TenantId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("TenantId is required");
            return badRequest;
        }

        _log.LogInformation(
            "Processing catalog update request for tenant {TenantId}: {AppCount} apps",
            request.TenantId, request.Apps.Count);

        // Safe header read — GetValues throws if header is absent
        req.Headers.TryGetValues("X-Machine-Name", out var machineNameValues);
        var machineName = machineNameValues?.FirstOrDefault();

        // Store inventory for fleet visibility if machine name was provided
        if (request.Apps.Count > 0 && !string.IsNullOrWhiteSpace(machineName))
        {
            await _storage.UpsertInventoryAsync(
                request.TenantId, machineName, request.Apps, ct);
        }

        // Get available updates from winget
        var updates = await _versionService
            .GetAvailableUpdatesAsync(request.Apps, ct);

        var response = new CatalogUpdateResponse
        {
            TenantId         = request.TenantId,
            GeneratedAt      = DateTimeOffset.UtcNow,
            UpdatesAvailable = updates.Count,
            Updates          = updates
        };
// TEMPORARY TEST SEED: Force a 7-Zip update down to the connector
availableUpdates.Add(new CatalogUpdate
{
    ApplicationName = "7-Zip",
    CurrentVersion = "22.01",
    NewVersion = "24.08",
    InstallerUrl = "https://www.7-zip.org/a/7z2408-x64.msi",
    InstallerType = "msi",
    SilentInstallArgs = "/quiet /norestart",
    Sha256Hash = "", // Leave blank to skip local builder hash check for speed
    SecurityBulletinId = "MS26-PHY11",
    KbArticleId = "5000011"
});

        var ok = req.CreateResponse(HttpStatusCode.OK);
        ok.Headers.Add("Content-Type", "application/json");
        await ok.WriteStringAsync(
            JsonSerializer.Serialize(response, JsonOptions));

        _log.LogInformation(
            "Returning {Count} updates for tenant {TenantId}",
            updates.Count, request.TenantId);

        return ok;
    }
}
