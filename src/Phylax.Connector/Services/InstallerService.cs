using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.IO;

namespace Phylax.Connector.Services;

public class LocalInstallerService
{
    private readonly ILogger<LocalInstallerService> _log;

    public LocalInstallerService(ILogger<LocalInstallerService> log)
    {
        _log = log;
    }

    /// <summary>
    /// Executes a silent package upgrade via WinGet, resolving paths dynamically and preventing shell termination.
    /// </summary>
    public async Task<bool> InstallUpdateAsync(string appName, string targetVersion, CancellationToken ct)
    {
        _log.LogInformation("Initiating silent upgrade execution for {App} -> v{Version}...", appName, targetVersion);

        string wingetPath = ResolveWinGetPath();
        
        // If we found a specific path and it somehow vanished, abort. 
        // If it's just "winget.exe", we proceed and let the OS attempt path/alias resolution.
        if (wingetPath != "winget.exe" && !File.Exists(wingetPath))
        {
            _log.LogCritical("CRITICAL: WinGet engine is not installed or accessible on this host. Please re-run the Vanguard onboarding extension script ('Install-Vanguard.ps1').");
            return false;
        }

        string wingetId = MapToWinGetId(appName);

        // Safe deployment arguments:
        // - Removed '--force' to prevent Windows Restart Manager from forcibly killing explorer.exe or user sessions.
        // - Added '--disable-interactivity' and '--include-unknown' for headless server execution.
        string arguments = $"upgrade --id {wingetId} --version {targetVersion} --silent --accept-source-agreements --accept-package-agreements --scope machine --include-unknown --disable-interactivity";

        var startInfo = new ProcessStartInfo
        {
            FileName = wingetPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            _log.LogDebug("Spawning process: {File} {Args}", startInfo.FileName, startInfo.Arguments);

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            // Enforce a 20-minute hard timeout to prevent hung interactive installers from locking the agent
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(20));

            await process.WaitForExitAsync(cts.Token);

            string stdOut = await process.StandardOutput.ReadToEndAsync(ct);
            string stdErr = await process.StandardError.ReadToEndAsync(ct);

            if (!string.IsNullOrWhiteSpace(stdOut))
                _log.LogDebug("WinGet Output for {App}: {Output}", appName, stdOut.Trim());

            return EvaluateExitCode(process.ExitCode, appName, targetVersion, stdErr);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to execute installer process for {App} using path '{Path}'.", appName, wingetPath);
            return false;
        }
    }

    /// <summary>
    /// Evaluates standard WinGet and Windows Installer (MSI/Inno/Nullsoft) exit codes.
    /// </summary>
    private bool EvaluateExitCode(int exitCode, string appName, string targetVersion, string stdErr)
    {
        switch (exitCode)
        {
            case 0: // S-OK
                _log.LogInformation("Successfully patched {App} to version {Version}.", appName, targetVersion);
                return true;

            case 3010: // ERROR_SUCCESS_REBOOT_REQUIRED
            case 1641: // ERROR_SUCCESS_REBOOT_INITIATED
            case -1978335213: // WinGet 0x8A150013 (APPREBOOTREQUIRED)
                _log.LogWarning("PATCH SUCCESS (REBOOT REQUIRED): {App} v{Version} installed successfully. A system reboot is required to complete file replacements.", appName, targetVersion);
                return true;

            case -1978335189: // WinGet 0x8A15002B (NO_APPLICABLE_UPDATE)
                _log.LogInformation("WinGet reports {App} is already at or above target version v{Version}. No changes made.", appName, targetVersion);
                return true;

            case 1618: // ERROR_INSTALL_ALREADY_RUNNING (MSI Mutex Lock)
                _log.LogWarning("INSTALL DEFERRED: Another MSI installation is currently active on the machine. {App} will retry next cycle.", appName);
                return false;

            default:
                _log.LogError("Installer failed for {App} with exit code: {Code} (0x{Hex:X8}). Error Output: {Err}",
                    appName, exitCode, exitCode, stdErr.Trim());
                return false;
        }
    }

    /// <summary>
    /// Dynamically hunts for winget.exe via standard environment paths and App Execution Aliases.
    /// STRICTLY prohibits bypassing the UWP sandbox to prevent 0xC0000135 DLL load failures.
    /// </summary>
    private string ResolveWinGetPath()
    {
        // 1. Check current user (or SYSTEM) AppData WindowsApps alias path
        var userWindowsApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");

        if (File.Exists(userWindowsApps))
            return userWindowsApps;

        // 2. Iterate through all directories in the machine and user PATH environment variables
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var fullPath = Path.Combine(dir.Trim(), "winget.exe");
                if (File.Exists(fullPath))
                    return fullPath;
            }
            catch (ArgumentException)
            {
                // Ignore malformed paths in environment variables
            }
        }

        // 3. Fallback to base executable command to let the OS attempt alias resolution.
        // We explicitly DO NOT search C:\Program Files\WindowsApps\ to avoid UWP sandbox violations.
        return "winget.exe";
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
