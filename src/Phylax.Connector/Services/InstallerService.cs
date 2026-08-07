using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Phylax.Connector.Services;

public class InstallerService
{
    private readonly ILogger<InstallerService> _log;

    public InstallerService(ILogger<InstallerService> log)
    {
        _log = log;
    }

    public async Task<bool> InstallUpdateAsync(string appName, string targetVersion, CancellationToken ct)
    {
        _log.LogInformation("Initiating silent upgrade execution for {App}...", appName);

        string wingetId = MapToWinGetId(appName);

        var startInfo = new ProcessStartInfo
        {
            FileName = "winget.exe",
            Arguments = $"upgrade --id {wingetId} --version {targetVersion} --silent --force --accept-source-agreements --accept-package-agreements --scope machine",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(20));

            await process.WaitForExitAsync(cts.Token);

            if (process.ExitCode == 0)
            {
                _log.LogInformation("Successfully patched {App} to version {Version}.", appName, targetVersion);
                return true;
            }

            if (process.ExitCode == 1618)
            {
                _log.LogWarning("Installation deferred: Another MSI installation is currently active.");
            }
            else if (process.ExitCode == 3010 || process.ExitCode == 1641)
            {
                _log.LogInformation("{App} update completed successfully. System reboot requested.", appName);
                return true;
            }

            _log.LogError("Installer failed for {App} with exit code: {Code}", appName, process.ExitCode);
            return false;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to execute installer process for {App}.", appName);
            return false;
        }
    }

    private string MapToWinGetId(string appName)
    {
        if (appName.Contains("Notepad++", StringComparison.OrdinalIgnoreCase)) return "Notepad++.Notepad++";
        if (appName.Contains("7-Zip", StringComparison.OrdinalIgnoreCase)) return "7zip.7zip";
        if (appName.Contains("PuTTY", StringComparison.OrdinalIgnoreCase)) return "PuTTY.PuTTY";
        if (appName.Contains("Git", StringComparison.OrdinalIgnoreCase)) return "Git.Git";
        return appName;
    }
}
