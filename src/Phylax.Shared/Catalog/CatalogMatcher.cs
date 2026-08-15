using System;
using System.Collections.Generic;
using Phylax.Shared.Models;

namespace Phylax.Shared.Catalog
{
    /// <summary>
    /// IMPORTANT DESIGN NOTE: Defender for Endpoint's vulnerability API reports ProductName/VendorName
    /// as normalized CPE-style values (e.g. "acrobat_reader_dc" / "adobe"), NOT the registry
    /// DisplayName strings ("Adobe Acrobat Reader DC (64-bit)") that KnownAppCatalog.cs matches
    /// against. They are two different vocabularies for the same software, so this needs its own
    /// mapping table rather than reusing KnownAppCatalog's regexes directly.
    ///
    /// The values below are placeholders based on Defender's typical naming convention — pull a
    /// real sample from your tenant (GET /api/vulnerabilities/machinesVulnerabilities against your
    /// test boxes) and correct these against actual ProductName/VendorName values before trusting
    /// this for auto-remediation. Treat this file as the one you'll iterate on the most.
    /// </summary>
    public static class CatalogMatcher
    {
        private record DefenderMappingRule(string VendorContains, string ProductContains, string WingetId, string ApplicationName, string InstallerType);

        private static readonly List<DefenderMappingRule> Rules = new()
        {
            new("adobe", "acrobat_reader", "Adobe.Acrobat.Reader.64-bit", "Adobe Acrobat Reader DC", "exe"),
            new("7-zip", "7-zip", "7zip.7zip", "7-Zip", "msi"),
            new("notepad", "notepad", "Notepad++.Notepad++", "Notepad++", "exe"),
            new("microsoft", "sql_server_management_studio", "Microsoft.SQLServerManagementStudio", "SQL Server Management Studio", "exe"),
            new("google", "chrome", "Google.Chrome", "Google Chrome", "msi"),
            new("microsoft", "visual_studio_code", "Microsoft.VisualStudioCode", "Visual Studio Code", "exe"),
            new("git", "git", "Git.Git", "Git", "exe"),
        };

        /// <summary>Attempts to resolve a Defender VulnerabilityFinding to a WinGet ID + display name.</summary>
        public static (string WingetId, string ApplicationName, string InstallerType)? TryResolve(VulnerabilityFinding finding)
        {
            foreach (var rule in Rules)
            {
                if (finding.VendorName.Contains(rule.VendorContains, StringComparison.OrdinalIgnoreCase) &&
                    finding.ProductName.Contains(rule.ProductContains, StringComparison.OrdinalIgnoreCase))
                {
                    return (rule.WingetId, rule.ApplicationName, rule.InstallerType);
                }
            }

            return null;
        }
    }
}
