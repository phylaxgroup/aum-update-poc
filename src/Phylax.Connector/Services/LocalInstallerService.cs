using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Phylax.Connector.Models;

namespace Phylax.Connector.Services;

public class LocalInstallerService
{
    private readonly ILogger<LocalInstallerService> _log;

    public LocalInstallerService(ILogger<LocalInstallerService> log)
    {
        _log = log;
    }

    /// <summary>
    /// Runs "winget upgrade" locally for the given catalog update. Requires update.WingetId
    /// to be populated — falls back to failure (not a name-based guess) if it's empty, since
    /// resolving by display name is exactly the kind of silent-wrong-package risk this exists
    /// to avoid.
    /// </summary>
    public async Task<bool> InstallUpdateAsync(CatalogUpdate update, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(update.WingetId))
        {
            _log.LogWarning("{App} has no WingetId set — skipping local install rather than guessing by name.", update.ApplicationName);
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "winget",
            Arguments = $"upgrade --id \"{update.WingetId}\" --silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _log.LogInformation("Running: winget {Args}", psi.Arguments);

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);

        // 0x8A15002B = "no applicable update found" (already current) — treat as success.
        bool success = process.ExitCode == 0 || process.ExitCode == unchecked((int)0x8A15002B);

        if (!success)
        {
            _log.LogError("winget exited {Code} for {WingetId}: {Error}", process.ExitCode, update.WingetId, stderr.ToString());
        }

        return success;
    }
}