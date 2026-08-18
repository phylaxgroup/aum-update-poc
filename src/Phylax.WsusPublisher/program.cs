using System;
using System.Collections.Generic;

namespace Phylax.WsusPublisher
{
    /// <summary>
    /// Standalone net48 publisher invoked by the net8 Phylax.Connector via Process.Start.
    ///
    /// Exit codes (the connector reads these):
    ///   0 = published successfully
    ///   1 = bad/missing arguments
    ///   2 = download or hash verification failed
    ///   3 = SDP build failed
    ///   4 = WSUS publish failed
    ///   5 = published, but approval failed (package IS in WSUS - approve manually)
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var a = ParseArgs(args);

            if (a.Count == 0 || a.ContainsKey("help") || a.ContainsKey("?"))
            {
                PrintUsage();
                return 1;
            }

            string appName = Get(a, "app");
            string version = Get(a, "version");
            string installerUrl = Get(a, "url");

            if (string.IsNullOrWhiteSpace(appName) ||
                string.IsNullOrWhiteSpace(version) ||
                string.IsNullOrWhiteSpace(installerUrl))
            {
                Console.Error.WriteLine("ERROR: --app, --version and --url are all required.");
                PrintUsage();
                return 1;
            }

            var request = new PublishRequest
            {
                ApplicationName    = appName,
                NewVersion         = version,
                InstallerUrl       = installerUrl,
                InstallerType      = Get(a, "type", "msi"),
                SilentInstallArgs  = Get(a, "args", ""),
                Sha256Hash         = Get(a, "sha256", ""),
                WingetId           = Get(a, "wingetid", ""),
                KbArticleId        = Get(a, "kb", "5000000"),
                SecurityBulletinId = Get(a, "bulletin", "MS26-PHY00"),
                VendorName         = Get(a, "vendor", "Phylax"),
                ProductName        = Get(a, "product", "Phylax Third-Party Updates"),
                StagingDirectory   = Get(a, "staging", @"C:\ProgramData\Phylax\Staging"),
                WsusServerName     = Get(a, "wsusserver", ""),   // empty = localhost
                WsusPort           = int.Parse(Get(a, "wsusport", "8530")),
                WsusUseSsl         = bool.Parse(Get(a, "wsusssl", "false")),
                ApprovalGroup      = Get(a, "approve", "")       // empty = do not approve
            };

            var publisher = new WsusPackagePublisher();
            return publisher.Publish(request);
        }

        private static Dictionary<string, string> ParseArgs(string[] args)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--")) continue;

                string key = args[i].Substring(2);
                string value = (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    ? args[++i]
                    : "true";

                result[key] = value;
            }

            return result;
        }

        private static string Get(Dictionary<string, string> a, string key, string fallback = "")
        {
            string v;
            return a.TryGetValue(key, out v) ? v : fallback;
        }

        private static void PrintUsage()
        {
            Console.WriteLine(@"
Phylax.WsusPublisher - publishes a third-party update to WSUS as an SDP package.

Required:
  --app <name>          Application display name, e.g. ""7-Zip""
  --version <ver>       New version, e.g. ""24.08""
  --url <url>           Installer download URL

Optional:
  --type <msi|exe>      Installer type (default: msi)
  --args <switches>     Silent install switches, e.g. ""/quiet /norestart""
  --sha256 <hash>       Expected SHA256 of the installer (skipped if omitted)
  --wingetid <id>       WinGet package ID (used for staging folder naming)
  --kb <id>             KB article ID injected into the SDP (default: 5000000)
  --bulletin <id>       Security bulletin ID (default: MS26-PHY00)
  --vendor <name>       SDP vendor name (default: Phylax)
  --product <name>      SDP product name (default: Phylax Third-Party Updates)
  --staging <dir>       Payload staging directory (default: C:\ProgramData\Phylax\Staging)
  --wsusserver <name>   WSUS server (default: localhost)
  --wsusport <port>     WSUS port (default: 8530)
  --wsusssl <bool>      Use SSL (default: false)
  --approve <group>     Approve for this target group after publish (default: no approval)

Exit codes: 0=ok 1=bad args 2=download failed 3=sdp failed 4=publish failed 5=published-but-not-approved
");
        }
    }

    internal class PublishRequest
    {
        public string ApplicationName;
        public string NewVersion;
        public string InstallerUrl;
        public string InstallerType;
        public string SilentInstallArgs;
        public string Sha256Hash;
        public string WingetId;
        public string KbArticleId;
        public string SecurityBulletinId;
        public string VendorName;
        public string ProductName;
        public string StagingDirectory;
        public string WsusServerName;
        public int WsusPort;
        public bool WsusUseSsl;
        public string ApprovalGroup;
    }
}