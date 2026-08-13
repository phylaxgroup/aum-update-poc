using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace Phylax.FunctionApp.Functions;

public class ConnectorStatusFunction
{
    [Function("ConnectorStatus")]
    public IActionResult Run([HttpTrigger(AuthorizationLevel.Function, "post", Route = "connector/status")] HttpRequest req)
    {
        return new OkResult();
    }
}
