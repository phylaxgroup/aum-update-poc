using System.Net.Http.Json;
using System.Text.Json;
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

    public async Task<List<AvailableUpdate>> GetRequiredUpdatesAsync(
        List<InstalledApp> inventory, 
        CancellationToken ct = default)
    {
        try
        {
            var requestPayload = new CatalogUpdateRequest
            {
                TenantId = _options.TenantId,
                Timestamp = DateTimeOffset.UtcNow,
                Apps = inventory.Select(a => new AppInventoryItem
                {
                    DisplayName = a.DisplayName ?? "",
                    DisplayVersion = a.DisplayVersion ?? "",
                    Publisher = a.Publisher ?? "",
                    ProductCode = a.ProductCode ?? "",
                    WingetId = a.WingetId ?? ""
                }).ToList()
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
            return result?.Updates ?? new List<AvailableUpdate>();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to retrieve catalog updates from Function App.");
            return new List<AvailableUpdate>();
        }
    }
}

// ============================================================================
// WIRE SERIALIZATION SUPPORT MODELS
// ============================================================================
public class CatalogUpdateRequest
{
    public string TenantId { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public List<AppInventoryItem> Apps { get; set; } = new();
}

public class AppInventoryItem
{
    public string DisplayName { get; set; } = string.Empty;
    public string DisplayVersion { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public string WingetId { get; set; } = string.Empty;
}

public class CatalogUpdateResponse
{
    public List<AvailableUpdate> Updates { get; set; } = new();
}
