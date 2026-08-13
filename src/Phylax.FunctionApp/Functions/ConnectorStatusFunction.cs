using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Phylax.FunctionApp.Models;

namespace Phylax.FunctionApp.Functions;

public class ConnectorStatusFunction
{
    private readonly ILogger<ConnectorStatusFunction> _logger;

    public ConnectorStatusFunction(ILogger<ConnectorStatusFunction> logger)
    {
        _logger = logger;
    }

    [Function("ConnectorStatus")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "connector/status")] HttpRequestData req)
    {
        _logger.LogInformation("Receiving connector telemetry status update.");

        var status = await req.ReadFromJsonAsync<ConnectorStatusRequest>();
        if (status is null)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        _logger.LogInformation("Connector Execution Summary -> Machine: {Machine}, App: {App}, Success: {Status}", 
            status.MachineName, status.ApplicationName, status.Success);

        return req.CreateResponse(HttpStatusCode.OK);
    }
}
