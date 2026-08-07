using System.Net.Http.Headers;

public async Task<string?> GetArcManagedIdentityTokenAsync(CancellationToken ct)
{
    using var client = new HttpClient();
    // The local Arc Guest Agent identity endpoint port is fixed at 40342
    var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost:40342/metadata/identity/oauth2/token?api-version=2019-11-01&resource=https://management.azure.com/");
    
    // The HIMDS endpoint strictly requires this metadata header to prevent SSRF attacks
    request.Headers.Add("Metadata", "true");

    try
    {
        var response = await client.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(ct);
            // Parse out the access_token string from the JSON response
            var tokenResponse = System.Text.Json.JsonDocument.Parse(content);
            return tokenResponse.RootElement.GetProperty("access_token").GetString();
        }
    }
    catch (Exception)
    {
        // Fallback or log if running in a non-Arc environment
    }
    return null;
}
