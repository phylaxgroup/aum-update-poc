using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Ingestion;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Phylax.FunctionApp
{
    public class CatalogUpdates
    {
        private readonly ILogger<CatalogUpdates> _logger;

        public CatalogUpdates(ILogger<CatalogUpdates> logger)
        {
            _logger = logger;
        }

        [Function("CatalogUpdates")]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "catalog/updates")] HttpRequest req)
        {
            _logger.LogInformation("CatalogUpdates HTTP trigger endpoint invoked.");

            // 1. Read and parse incoming Vanguard inventory payload
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(requestBody))
            {
                _logger.LogWarning("Received empty request body payload.");
                return new BadRequestObjectResult("Request body cannot be empty.");
            }

            // 2. Fetch Azure Monitor Ingestion configuration from environment variables
            var endpointUri = Environment.GetEnvironmentVariable("DataCollectionEndpoint");
            var ruleId = Environment.GetEnvironmentVariable("DataCollectionRuleId");
            var streamName = Environment.GetEnvironmentVariable("StreamName");

            if (string.IsNullOrEmpty(endpointUri) || string.IsNullOrEmpty(ruleId) || string.IsNullOrEmpty(streamName))
            {
                _logger.LogError("Ingestion failure: Missing required DCR/DCE environment variables in App Settings.");
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }

            // 3. Forward the raw JSON array string straight to Log Analytics
            try
            {
                _logger.LogInformation("Initializing LogsIngestionClient to endpoint: {Endpoint}", endpointUri);
                
                var endpoint = new Uri(endpointUri);
                // Automatically leverages the Function App's System-Assigned Managed Identity
                var credential = new DefaultAzureCredential();
                var client = new LogsIngestionClient(endpoint, credential);

                _logger.LogInformation("Uploading payload to DCR stream: {StreamName}...", streamName);
                
                // Azure Monitor Ingestion expects a UTF-8 JSON array matching the DCR schema
                var requestContent = RequestContent.Create(requestBody);
                var response = await client.UploadAsync(ruleId, streamName, requestContent);

                if (response.IsError)
                {
                    _logger.LogError("Log Analytics ingestion failed. HTTP Status: {Status}", response.Status);
                    return new StatusCodeResult(StatusCodes.Status502BadGateway);
                }

                _logger.LogInformation("Telemetry successfully written to Log Analytics workspace.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unhandled exception occurred during Log Analytics pipeline ingestion.");
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }

            // 4. Return clean V1 compliance state back to the Vanguard agent
            // (Server-side dynamic version evaluations will map directly over this workspace in V2)
            var mockResponse = new
            {
                Message = "Inventory successfully processed by cloud gateway.",
                AvailableUpdatesCount = 0,
                Updates = new List<object>()
            };

            return new OkObjectResult(mockResponse);
        }
    }
}
