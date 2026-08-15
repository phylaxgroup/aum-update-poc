using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Phylax.Shared.Catalog
{
    /// <summary>
    /// Maps standard Windows Registry DisplayNames and Publishers to official WinGet Package IDs.
    ///
    /// Moved here from Phylax.FunctionApp/Services/KnownAppCatalog.cs - it was declared
    /// `namespace Phylax.Connector.Services` while physically living in the FunctionApp project,
    /// which is why it wasn't wired into anything there. Now both the connector (registry-based
    /// matching) and the remediation function (Defender-based matching, via CatalogMatcher) share
    /// the same rule set instead of drifting into two copies.
    /// </summary>
    public static class KnownAppCatalog
    {
        private record AppMappingRule(
            string WingetId,
            Regex DisplayNamePattern,
            string? RequiredPublisher = null
        );

        private static readonly List<AppMappingRule> Rules = new()
        {
            // --- Notepad++ ---
            new("Notepad++.Notepad++",
                new Regex(@"^Notepad\+\+", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

            // --- 7-Zip ---
            new("7zip.7zip",
                new Regex(@"^7-Zip", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

            // --- Adobe Reader / Acrobat ---
            new("Adobe.Acrobat.Reader.64-bit",
                new Regex(@"^Adobe Acrobat (Reader|DC).*\(64-bit\)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "Adobe Systems"),
            new("Adobe.AdobeAcrobatReaderDC",
                new Regex(@"^Adobe Acrobat Reader DC", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "Adobe Systems"),

            // --- Ghostscript ---
            new("ArtifexSoftware.Ghostscript",
                new Regex(@"^GPL Ghostscript|^Ghostscript", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

            // --- SQL Server Management Studio (SSMS) ---
            new("Microsoft.SQLServerManagementStudio",
                new Regex(@"^Microsoft SQL Server Management Studio", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "Microsoft Corporation"),

            // --- Visual Studio Code ---
            new("Microsoft.VisualStudioCode",
                new Regex(@"^Microsoft Visual Studio Code", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "Microsoft Corporation"),

            // --- Other common admin utilities ---
            new("Git.Git",
                new Regex(@"^Git(\s+version)?", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            new("PuTTY.PuTTY",
                new Regex(@"^PuTTY release", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            new("WinSCP.WinSCP",
                new Regex(@"^WinSCP", RegexOptions.IgnoreCase | RegexOptions.Compiled))
        };

        /// <summary>Matches an installed registry entry (DisplayName/Publisher) to a known WinGet ID.</summary>
        public static string? TryResolveWingetId(string displayName, string? publisher)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return null;

            foreach (var rule in Rules)
            {
                if (rule.RequiredPublisher != null &&
                    !string.IsNullOrWhiteSpace(publisher) &&
                    publisher!.IndexOf(rule.RequiredPublisher, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (rule.DisplayNamePattern.IsMatch(displayName))
                {
                    return rule.WingetId;
                }
            }

            return null;
        }
    }
}