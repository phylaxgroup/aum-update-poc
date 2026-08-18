using System.Net.Http.Json;
using System.Text.Json;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phylax.Connector.Configuration;
using Phylax.Connector.Models;

namespace Phylax.Connector.Services;

public class CatalogClient
{
    private readonly HttpClient _http;
    private readonly ILogger<CatalogClient> _log;
    private readonly PhylaxConnectorOptions _options;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public CatalogClient(
        HttpClient http,
        ILogger<CatalogClient> log,
        IOptions<PhylaxConnectorOptions> options)
    {
        _http = http;
        _log = log;
        _options = options.Value;
    }

    public async Task<List<CatalogUpdate>> GetRequiredUpdatesAsync(
        List<InstalledApp> inventory,
        CancellationToken ct = default)
    {
        try
        {
            // Shape MUST match Phylax.FunctionApp.Models.CatalogUpdateRequest exactly.
            // Previously this sent { TenantId, Timestamp, Apps } while the Function App
            // deserialized { MachineName, Domain, InstalledApplications } - no property
            // names lined up, so InstalledApplications was always null server-side and
            // every request silently fell into the "empty payload" branch.
            var requestPayload = new CatalogUpdateRequest
            {
                MachineName = Environment.MachineName,
                Domain = Environment.UserDomainName,
                InstalledApplications = inventory
            };

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, "api/catalog/updates");
            requestMessage.Headers.Add("X-Tenant-Id", _options.TenantId);
            requestMessage.Headers.Add("X-Machine-Name", Environment.MachineName);
            requestMessage.Content = JsonContent.Create(requestPayload);

            _log.LogInformation("Sending {Count} inventory items to Phylax Catalog API...", inventory.Count);

            var response = await _http.SendAsync(requestMessage, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<CatalogUpdateResponse>(JsonOptions, ct);

            _log.LogInformation("Catalog API returned {Count} available updates.", result?.Updates.Count ?? 0);
            return result?.Updates ?? new List<CatalogUpdate>();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to retrieve catalog updates from Function App.");
            return new List<CatalogUpdate>();
        }
    }
}