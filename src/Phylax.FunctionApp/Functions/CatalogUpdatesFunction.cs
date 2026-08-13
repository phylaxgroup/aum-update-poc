using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Phylax.FunctionApp.Models;
using Phylax.FunctionApp.Services;

namespace Phylax.FunctionApp.Functions;

public class CatalogUpdatesFunction
{
    private readonly ILogger<CatalogUpdatesFunction> _logger;
    private readonly WingetManifestService _manifestService;
    private readonly VersionComparisonService _versionService;

    // Notice: InventoryStorageService is completely removed from this constructor
    public CatalogUpdatesFunction(
        ILogger<CatalogUpdatesFunction> logger,
        WingetManifestService manifestService,
        VersionComparisonService versionService)
    {
        _logger = logger;
        _manifestService = manifestService;
        _versionService = versionService;
    }

    [Function("CatalogUpdates")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "catalog/updates")] HttpRequestData req)
    {
        _logger.LogInformation("Processing update evaluation request.");

        var requestData = await req.ReadFromJsonAsync<CatalogUpdateRequest>();
        if (requestData is null || requestData.InstalledApplications.Count == 0)
        {
            var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badResponse.WriteStringAsync("Invalid or empty payload.");
            return badResponse;
        }

        var availableUpdates = new List<CatalogUpdate>();

        // TEMPORARY TEST SEED: Force a 7-Zip update down to the connector
        availableUpdates.Add(new CatalogUpdate
        {
            ApplicationName = "7-Zip",
            CurrentVersion = "22.01",
            NewVersion = "24.08",
            InstallerUrl = "https://www.7-zip.org/a/7z2408-x64.msi",
            InstallerType = "msi",
            SilentInstallArgs = "/quiet /norestart",
            Sha256Hash = "", // Left blank to bypass local hash validation for the test
            SecurityBulletinId = "MS26-PHY11",
            KbArticleId = "5000011"
        });

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new CatalogUpdateResponse { Updates = availableUpdates });
        return response;
    }
}
