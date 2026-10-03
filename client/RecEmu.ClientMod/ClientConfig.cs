using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MelonLoader;

namespace RecEmu.ClientMod
{
    /// <summary>
    /// Reads <c>RecEmuRedirect.cfg</c> from the MelonLoader user data directory.
    ///
    /// Everything the mod touches is configurable because the Rec Room client is rebuilt often, the
    /// private server's hostname is chosen by whoever runs it, and Photon addresses depend on where
    /// the load balancer actually lives. A hardcoded value in a DLL is the wrong place for any of
    /// that.
    /// </summary>
    internal static class ClientConfig
    {
        public const string ModVersion = "1.0.0";

        private static readonly string FilePath = Path.Combine(
            Path.GetDirectoryName(typeof(ClientConfig).Assembly.Location)!, "RecEmuRedirect.cfg");

        private static Dictionary<string, string> Values { get; } = Load();

        public static string BaseUrl => Get("BaseUrl", "https://www.rec.local");

        public static string PhotonServer => Get("PhotonServer", "127.0.0.1");

        public static int PhotonPort => GetInt("PhotonPort", 5000);

        public static bool PhotonUseWss => GetBool("PhotonUseWss", true);

        public static string AppIdRealtime => Get("AppIdRealtime", "ce298166-7e52-4c47-a1c2-0ce4b7a7cfcb");

        public static string AppIdVoice => Get("AppIdVoice", "5fa7359e-b671-4910-8226-cb539cdae485");

        /// <summary>Region string reported to the load balancer. "localhost" means "same host".</summary>
        public static string PhotonRegion => Get("PhotonRegion", "localhost");

        /// <summary>Accept any TLS certificate. Needed for self-signed dev certs; leave off in public deployments.</summary>
        public static bool SkipCertificateValidation => GetBool("SkipCertificateValidation", true);

        /// <summary>
        /// Comma separated list of "TypeName::MemberName" pairs whose string getters return the API
        /// base URL. Empty by default because the hosts file is the more reliable redirect.
        /// </summary>
        public static IReadOnlyList<string> UrlMembers
        {
            get
            {
                var raw = Get("UrlMembers", string.Empty);
                return raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                          .Select(s => s.Trim())
                          .Where(s => s.Contains("::"))
                          .ToList();
            }
        }

        private static string Get(string key, string fallback)
            => Values.TryGetValue(key, out var value) && value.Length > 0 ? value : fallback;

        private static int GetInt(string key, int fallback)
            => Values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;

        private static bool GetBool(string key, bool fallback)
            => Values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;

        private static Dictionary<string, string> Load()
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (File.Exists(FilePath))
            {
                foreach (var raw in File.ReadAllLines(FilePath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;
                    var split = line.IndexOf('=');
                    if (split <= 0) continue;
                    values[line.Substring(0, split).Trim()] = line.Substring(split + 1).Trim().Trim('"');
                }

                MelonLogger.Msg($"[RecEmu] Loaded configuration from {FilePath}");
            }
            else
            {
                MelonLogger.Warning($"[RecEmu] No RecEmuRedirect.cfg found, using defaults. Expected at {FilePath}");
            }

            return values;
        }

        /// <summary>Writes the default configuration file so players have something to edit.</summary>
        public static void WriteTemplate()
        {
            if (File.Exists(FilePath)) return;

            try
            {
                File.WriteAllText(FilePath, string.Join(Environment.NewLine, new[]
                {
                    "# Rec Room -> private server redirect configuration.",
                    "# Written automatically on first launch; edit and restart the game to apply.",
                    string.Empty,
                    $"BaseUrl={BaseUrl}",
                    "  # Host serving the RecNet API. Wildcard DNS or a hosts file entry must point at it,",
                    "  # because the client requests several subdomains (*.rec.local by default).",
                    string.Empty,
                    $"PhotonServer={PhotonServer}",
                    "PhotonPort=" + PhotonPort,
                    "PhotonUseWss=" + PhotonUseWss,
                    "PhotonRegion=" + PhotonRegion,
                    "AppIdRealtime=" + AppIdRealtime,
                    "AppIdVoice=" + AppIdVoice,
                    string.Empty,
                    "SkipCertificateValidation=" + SkipCertificateValidation,
                    "  # Only for self-signed development certificates. Prefer trusting the",
                    "  # certificate or terminating TLS in front of the server in a real deployment.",
                    string.Empty,
                    "# Optional: redirect API URLs inside the client instead of via DNS/hosts.",
                    "# Type::Member pairs, comma separated. Only needed if the API host cannot be",
                    "# redirected by name, e.g. RecRoom.Api.ApiHelper::BaseUrl",
                    "UrlMembers=",
                }));

                MelonLogger.Msg($"[RecEmu] Wrote default configuration to {FilePath}");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RecEmu] Could not write configuration template: {ex.Message}");
            }
        }

        /// <summary>Every MelonLoader assembly, so type lookups can search beyond the game assembly.</summary>
        public static IEnumerable<Assembly> SearchableAssemblies()
        {
            var assemblies = new List<Assembly> { Assembly.GetExecutingAssembly() };

            assemblies.AddRange(AppDomain.CurrentDomain.GetAssemblies()
                                        .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.FullName)));

            return assemblies.Distinct();
        }
    }
}