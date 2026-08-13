using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Phylax.FunctionApp.Models;

namespace Phylax.FunctionApp.Functions;

public class CatalogUpdatesFunction
{
    [Function("CatalogUpdates")]
    public IActionResult Run([HttpTrigger(AuthorizationLevel.Function, "post", Route = "catalog/updates")] HttpRequest req)
    {
        var availableUpdates = new List<CatalogUpdate>
        {
            new CatalogUpdate
            {
                ApplicationName = "7-Zip",
                CurrentVersion = "22.01",
                NewVersion = "24.08",
                InstallerUrl = "https://www.7-zip.org/a/7z2408-x64.msi",
                InstallerType = "msi",
                SilentInstallArgs = "/quiet /norestart",
                Sha256Hash = "", 
                SecurityBulletinId = "MS26-PHY11",
                KbArticleId = "5000011"
            }
        };

        return new OkObjectResult(new CatalogUpdateResponse { Updates = availableUpdates });
    }
}
