using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Phylax.Shared.Models;

namespace Phylax.Shared.Delivery
{
    /// <summary>
    /// Pushes a winget upgrade to an Azure Arc-enabled server or native Azure IaaS VM via the
    /// Run Command management-plane API — no Vanguard agent required on the box. This is the
    /// "target Arc-enabled servers or IaaS instances" option from your notes: good for machines
    /// Defender flags that don't have Vanguard installed, or fleet-wide push without deploying
    /// an agent everywhere.
    ///
    /// Resource provider path differs slightly between the two:
    ///   Arc-enabled server: {resourceId}/runCommands/{name}   (Microsoft.HybridCompute)
    ///   Azure IaaS VM:       {resourceId}/runCommands/{name}   (Microsoft.Compute)
    /// Same shape, same script payload — DeliveryTarget.IsArcEnabled / IsAzureVm just pick the
    /// verb-path, both go through this one adapter.
    ///
    /// VERIFY BEFORE USE: the api-version below is illustrative — confirm the current
    /// Microsoft.HybridCompute / Microsoft.Compute runCommands api-version against
    /// learn.microsoft.com at implementation time, these rev periodically.
    /// </summary>
    public class WingetArcRunCommandAdapter : IPatchDeliveryAdapter
    {
        private const string HybridComputeApiVersion = "2023-10-03-preview"; // VERIFY
        private const string ComputeApiVersion = "2024-07-01"; // VERIFY

        private readonly HttpClient _httpClient;
        private readonly TokenCredential _credential;
        private readonly ILogger<WingetArcRunCommandAdapter> _logger;

        public string Name => "winget-arc-runcommand";

        public WingetArcRunCommandAdapter(HttpClient httpClient, TokenCredential credential, ILogger<WingetArcRunCommandAdapter> logger)
        {
            _httpClient = httpClient;
            _credential = credential;
            _logger = logger;
        }

        public bool CanHandle(DeliveryTarget target) =>
            !target.HasVanguardAgent && (target.IsArcEnabled || target.IsAzureVm) && !string.IsNullOrEmpty(target.AzureResourceId);

        public async Task<DeliveryResult> DeliverAsync(PatchCandidate candidate, DeliveryTarget target, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(candidate.WingetId))
            {
                return DeliveryResult.Fail(Name, $"{candidate.ApplicationName} has no WingetId — cannot resolve via winget.");
            }

            string apiVersion = target.IsArcEnabled ? HybridComputeApiVersion : ComputeApiVersion;
            string runCommandName = $"phylax-winget-{candidate.WingetId.Replace('.', '-')}-{DateTime.UtcNow:yyyyMMddHHmmss}";
            string url = $"https://management.azure.com{target.AzureResourceId}/runCommands/{runCommandName}?api-version={apiVersion}";

            // PowerShell script executed on the target via the Run Command extension.
            // Kept minimal and idempotent — winget itself no-ops if already current.
            string script =
                $"winget upgrade --id \"{candidate.WingetId}\" --silent " +
                "--accept-package-agreements --accept-source-agreements --disable-interactivity; " +
                "exit ($LASTEXITCODE -eq -1978335189 ? 0 : $LASTEXITCODE)"; // treat "no applicable update" as success

            var payload = new
            {
                properties = new
                {
                    source = new { script },
                    timeoutInSeconds = 900,
                    asyncExecution = false
                }
            };

            try
            {
                var token = await _credential.GetTokenAsync(
                    new TokenRequestContext(new[] { "https://management.azure.com/.default" }), ct);

                using var request = new HttpRequestMessage(HttpMethod.Put, url)
                {
                    Content = JsonContent.Create(payload)
                };
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);

                _logger.LogInformation("Submitting Run Command {Name} to {Resource}", runCommandName, target.AzureResourceId);

                using var response = await _httpClient.SendAsync(request, ct);
                string body = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    return DeliveryResult.Fail(Name, $"Run Command submit failed ({(int)response.StatusCode}): {body}");
                }

                // asyncExecution=false above means Azure blocks the PUT until the script finishes
                // and returns instanceView.status/output in the body — parse and surface it rather
                // than just trusting the HTTP 200 (the script itself can still have failed).
                using var doc = JsonDocument.Parse(body);
                bool scriptSucceeded = TryGetInstanceStatus(doc, out string status, out string output);

                return scriptSucceeded
                    ? DeliveryResult.Ok(Name, $"Run Command {status}: {output}")
                    : DeliveryResult.Fail(Name, $"Run Command reported {status}: {output}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Run Command delivery failed for {App} on {Machine}", candidate.ApplicationName, target.MachineName);
                return DeliveryResult.Fail(Name, ex.Message);
            }
        }

        private static bool TryGetInstanceStatus(JsonDocument doc, out string status, out string output)
        {
            status = "Unknown";
            output = "";
            try
            {
                var instanceView = doc.RootElement.GetProperty("properties").GetProperty("instanceView");
                status = instanceView.GetProperty("executionState").GetString() ?? "Unknown";
                if (instanceView.TryGetProperty("output", out var outputEl))
                {
                    output = outputEl.GetString() ?? "";
                }
                return string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase);
            }
            catch (KeyNotFoundException)
            {
                return false;
            }
        }
    }
}
