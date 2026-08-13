using System;

namespace Phylax.FunctionApp.Services;

public class VersionComparisonService
{
    public bool IsUpdateAvailable(string currentVersion, string latestVersion)
    {
        if (string.IsNullOrWhiteSpace(currentVersion) || string.IsNullOrWhiteSpace(latestVersion))
            return false;

        try
        {
            // Standard semantic version comparison block
            var current = new Version(NormalizeVersion(currentVersion));
            var latest = new Version(NormalizeVersion(latestVersion));

            return latest > current;
        }
        catch
        {
            // If string parsing fails, fall back to basic string inequality check
            return !currentVersion.Trim().Equals(latestVersion.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string NormalizeVersion(string version)
    {
        // Strips out letters/characters (like 'v1.2.3') keeping just digits and dots
        var clean = System.Text.RegularExpressions.Regex.Replace(version, @"[^\d\.]", "");
        var dots = clean.Split('.');
        
        // Ensure version contains at least major.minor
        if (dots.Length == 1) return $"{clean}.0";
        return clean;
    }
}
