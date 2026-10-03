using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace RecEmu.ClientMod
{
    /// <summary>
    /// The mod entry point.
    ///
    /// Rec Room ships as IL2CPP, which rules out the BepInEx approach of patching known game types:
    /// there are no usable compile-time type references for them. MelonLoader plus Harmony can patch
    /// IL2CPP methods, but only when the target is resolved at runtime by name. So nothing here
    /// references a game type; everything is looked up by string, and a lookup that fails is logged
    /// and skipped rather than thrown. A private-server patch that takes the game down on a rename is
    /// worse than one that quietly does nothing.
    /// </summary>
    public class Mod : MelonMod
    {
        public override void OnInitializeMelon()
        {
            MelonLogger.Msg($"[RecEmu] Rec Room redirect {ClientConfig.ModVersion} loading");

            ClientConfig.WriteTemplate();
            AllowSelfSignedCertificates();

            var harmony = new Harmony("com.recemu.redirect");

            RedirectPhotonServerSettings(harmony);
            RedirectPhotonVoiceClient(harmony);
            SkipNameServerLookup(harmony);
            RedirectApiUrls(harmony);

            MelonLogger.Msg($"[RecEmu] API base {ClientConfig.BaseUrl}, Photon {ClientConfig.PhotonServer}:{ClientConfig.PhotonPort} "
                          + $"(wss={ClientConfig.PhotonUseWss}, region={ClientConfig.PhotonRegion})");
            MelonLogger.Msg("[RecEmu] ready");
        }

        // ---------------------------------------------------------------------
        // Photon realtime
        // ---------------------------------------------------------------------

        /// <summary>
        /// Rewrites the app settings object right before PUN connects. Settings are patched rather
        /// than the settings file rewritten, because Rec Room's PhotonServerSettings asset is a
        /// ScriptableObject in the game's resources and editing it on disk means repacking bundles.
        /// </summary>
        private static void RedirectPhotonServerSettings(Harmony harmony)
        {
            var networkType = FindType("PhotonNetwork", "Photon.Pun.PhotonNetwork");
            var connect = FindMethod(networkType, "ConnectUsingSettings");
            if (connect is null)
            {
                MelonLogger.Warning("[RecEmu] PhotonNetwork.ConnectUsingSettings not found; realtime Photon was not redirected. "
                                  + "If rooms fail to load, check the game version this mod was built against.");
                return;
            }

            TryPatch(harmony, connect, nameof(PhotonConnectPrefix));

            MelonLogger.Msg("[RecEmu] Patched PhotonNetwork.ConnectUsingSettings");
        }

        /// <summary>
        /// Voice uses a separate client object with its own copy of the app id and address, created
        /// independently of PUN's, so the settings rewrite above does not cover it.
        /// </summary>
        private static void RedirectPhotonVoiceClient(Harmony harmony)
        {
            var voiceType = FindType("PhotonVoiceNetwork", "Photon.Voice.Unity.PhotonVoiceNetwork");
            var connect = FindMethod(voiceType, "Connect");
            if (connect is null)
            {
                MelonLogger.Warning("[RecEmu] PhotonVoiceNetwork.Connect not found; voice was not redirected.");
                return;
            }

            TryPatch(harmony, connect, nameof(PhotonVoiceConnectPrefix));

            MelonLogger.Msg("[RecEmu] Patched PhotonVoiceNetwork.Connect");
        }

        /// <summary>
        /// Photon clients configured with UseNameServer call a name server before the master server
        /// to find the best region. A self-hosted load balancer answers neither, so the call either
        /// hangs until timeout or throws and aborts the connection. Both settings below already point
        /// the client at a fixed address, so the lookup is pure overhead.
        /// </summary>
        private static void SkipNameServerLookup(Harmony harmony)
        {
            var clientType = FindType("LoadBalancingClient", "Photon.Realtime.LoadBalancingClient");
            var connect = FindMethod(clientType, "ConnectToNameServer");
            if (connect is null)
            {
                MelonLogger.Msg("[RecEmu] LoadBalancingClient.ConnectToNameServer not present; nothing to bypass");
                return;
            }

            TryPatch(harmony, connect, nameof(NameServerPrefix), nameof(NameServerPostfix));

            MelonLogger.Msg("[RecEmu] Bypassed Photon name server lookup");
        }

        // ---------------------------------------------------------------------
        // API host
        // ---------------------------------------------------------------------

        /// <summary>
        /// Redirects API URLs inside the client. Preferred path is DNS or a hosts file, which covers
        /// every subdomain with no client patching at all; this is the fallback for when the API
        /// hostname cannot be redirected by name. Only members explicitly named in UrlMembers are
        /// touched, because blanket-patching every string getter in the game would be reckless.
        /// </summary>
        private static void RedirectApiUrls(Harmony harmony)
        {
            var members = ClientConfig.UrlMembers;
            if (members.Count == 0)
            {
                MelonLogger.Msg("[RecEmu] No UrlMembers configured; relying on DNS/hosts for the API host");
                return;
            }

            foreach (var entry in members)
            {
                var parts = entry.Split(new[] { "::" }, StringSplitOptions.None);
                if (parts.Length != 2) continue;

                var type = FindType(parts[0]);
                var property = type?.GetProperty(parts[1],
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                var method = (property?.GetGetMethod(true) ?? FindMethod(type, "get_" + parts[1]))!;

                if (method is null)
                {
                    MelonLogger.Warning($"[RecEmu] Url member not found: {entry}");
                    continue;
                }

                TryPatch(harmony, method, nameof(UrlPrefix));
                MelonLogger.Msg($"[RecEmu] Patched {type!.FullName}.{parts[1]} -> {ClientConfig.BaseUrl}");
            }
        }

        // ---------------------------------------------------------------------
        // TLS
        // ---------------------------------------------------------------------

        /// <summary>
        /// Lets the game talk to a self-signed development certificate. This covers .NET-handled
        /// requests only; the native TLS stack used by Photon is unaffected, which is why the
        /// deployment instructions recommend installing the certificate locally rather than relying
        /// on this switch.
        /// </summary>
        private static void AllowSelfSignedCertificates()
        {
            if (!ClientConfig.SkipCertificateValidation) return;

            try
            {
                System.Net.ServicePointManager.ServerCertificateValidationCallback = (_, _, _, _) => true;
                MelonLogger.Msg("[RecEmu] Certificate validation disabled (development only)");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RecEmu] Could not relax certificate validation: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------------
        // Prefix bodies
        //
        // Signatures use object rather than game types because the game types cannot be named here.
        // Harmony matches __instance against the declaring type at runtime, so this is enough.
        // ---------------------------------------------------------------------

        /// <summary>Rewrites the PUN app settings object in place.</summary>
        internal static void PhotonConnectPrefix(object __instance)
        {
            var serverSettings = ReadStaticMember(FindType("PhotonNetwork", "Photon.Pun.PhotonNetwork"), "PhotonServerSettings")
                                 ?? ReadInstanceMember(__instance, "PhotonServerSettings");
            var appSettings = ReadMember(serverSettings, "AppSettings");
            if (appSettings is null)
            {
                MelonLogger.Warning("[RecEmu] Photon AppSettings not reachable; leaving Photon settings untouched");
                return;
            }

            WriteMember(appSettings, "Server", ClientConfig.PhotonServer);
            WriteMember(appSettings, "Port", ClientConfig.PhotonPort);
            WriteMember(appSettings, "Protocol", ClientConfig.PhotonUseWss ? "Wss" : "Ws");
            WriteMember(appSettings, "AppIdRealtime", ClientConfig.AppIdRealtime);
            WriteMember(appSettings, "AppIdVoice", ClientConfig.AppIdVoice);
            WriteMember(appSettings, "FixedRegion", ClientConfig.PhotonRegion);
            WriteMember(appSettings, "Region", ClientConfig.PhotonRegion);
            WriteMember(appSettings, "UseNameServer", false);

            MelonLogger.Msg($"[RecEmu] Photon pointed at {ClientConfig.PhotonServer}:{ClientConfig.PhotonPort}");
        }

        /// <summary>Rewrites the voice client's app id and address.</summary>
        internal static void PhotonVoiceConnectPrefix(object __instance)
        {
            var client = ReadInstanceMember(__instance, "Client");
            if (client is null) return;

            WriteMember(client, "AppId", ClientConfig.AppIdVoice);

            // LoadBalancingPeer.ServerAddress wants a full URI including the scheme and path.
            var peer = ReadMember(client, "LoadBalancingPeer");
            var scheme = ClientConfig.PhotonUseWss ? "wss" : "ws";
            var address = $"{scheme}://{ClientConfig.PhotonServer}:{ClientConfig.PhotonPort}";
            if (WriteMember(peer, "ServerAddress", address))
            {
                MelonLogger.Msg($"[RecEmu] Photon voice pointed at {address}");
            }
            else
            {
                MelonLogger.Warning("[RecEmu] LoadBalancingPeer.ServerAddress not found; voice may connect to the default host");
            }
        }

        /// <summary>Returns false to skip the name server call entirely.</summary>
        internal static bool NameServerPrefix() => false;

        /// <summary>Reports success to the caller that would have awaited the skipped call.</summary>
        internal static void NameServerPostfix(bool __result) => MelonLogger.Msg("[RecEmu] Name server lookup skipped");

        /// <summary>Replaces a URL string getter's result.</summary>
        internal static void UrlPrefix(ref string __result) => __result = ClientConfig.BaseUrl;

        // ---------------------------------------------------------------------
        // Reflection helpers
        //
        // Read and write go through property-then-field lookup because the same logical member is a
        // property in some builds and a field in others, and IL2CPP surfaces can change between
        // game versions.
        // ---------------------------------------------------------------------

        /// <summary>
        /// Applies a prefix and, optionally, a postfix named on this class. Skipping the original
        /// body is done by returning false from the prefix, not through ordering arguments.
        /// </summary>
        private static void TryPatch(Harmony harmony, MethodBase target, string prefixName, string? postfixName = null)
        {
            var prefix = typeof(Mod).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic);
            var postfix = postfixName is null
                ? null
                : typeof(Mod).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic);

            harmony.Patch(target,
                prefix: prefix is null ? null : new HarmonyMethod(prefix),
                postfix: postfix is null ? null : new HarmonyMethod(postfix));
        }

        private static Type? FindType(params string[] candidates)
        {
            foreach (var candidate in candidates)
            {
                var direct = Type.GetType(candidate);
                if (direct is not null) return direct;

                foreach (var assembly in ClientConfig.SearchableAssemblies())
                {
                    try
                    {
                        var found = assembly.GetType(candidate, false);
                        if (found is not null) return found;
                    }
                    catch
                    {
                        // Some game assemblies throw on enumeration while IL2CPP types are still
                        // being set up. Skipping one is fine; we keep looking.
                    }
                }

                // Rec Room is IL2CPP, so types generated for the game exist in the metadata of a
                // single assembly under names that do not always match the C# namespace.
                foreach (var assembly in ClientConfig.SearchableAssemblies())
                {
                    Type?[] types;
                    try { types = assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                    catch { continue; }

                    foreach (var type in types.Where(t => t is not null && t.Name == candidate))
                    {
                        return type!;
                    }
                }
            }

            return null;
        }

        private static MethodInfo? FindMethod(Type? type, string name)
        {
            if (type is null) return null;

            return type.GetMethod(name,
                       BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static,
                       null, Type.EmptyTypes, null)
                   ?? type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                          .FirstOrDefault(m => m.Name.StartsWith(name, StringComparison.Ordinal));
        }

        private static object? ReadStaticMember(Type? type, string name)
            => type is null ? null : ReadMember(type, name, staticOnly: true);

        private static object? ReadInstanceMember(object? instance, string name)
            => instance is null ? null : ReadMember(instance, name);

        private static object? ReadMember(object? target, string name, bool staticOnly = false)
        {
            if (target is null) return null;

            var type = target as Type ?? target.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy
                        | (target is Type ? BindingFlags.Static : BindingFlags.Instance);
            if (staticOnly) flags |= BindingFlags.Static;

            try
            {
                var property = type.GetProperty(name, flags);
                var getter = property?.GetGetMethod(true);
                if (getter is not null && getter.IsStatic && getter.GetParameters().Length == 0)
                {
                    return getter.Invoke(null, null);
                }

                var field = type.GetField(name, flags);
                if (field is not null) return field.GetValue(target is Type ? null : target);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RecEmu] Could not read {type.Name}.{name}: {ex.Message}");
            }

            return null;
        }

        private static bool WriteMember(object? target, string name, object? value)
        {
            if (target is null) return false;

            var type = target.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy | BindingFlags.Instance;

            try
            {
                var setter = type.GetProperty(name, flags)?.GetSetMethod(true);
                if (setter is not null && setter.GetParameters().Length == 1)
                {
                    setter.Invoke(target, new[] { Coerce(value, setter.GetParameters()[0].ParameterType) });
                    return true;
                }

                var field = type.GetField(name, flags);
                if (field is not null)
                {
                    field.SetValue(target, Coerce(value, field.FieldType));
                    return true;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RecEmu] Could not set {type.Name}.{name}: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Converts a config string into whatever the target member expects. Photon declares its
        /// connection protocol as an enum but its port as an int, and the config supplies both as
        /// text, so each one needs its own conversion.
        /// </summary>
        private static object? Coerce(object? value, Type targetType)
        {
            if (value is null) return null;
            if (targetType.IsInstanceOfType(value)) return value;

            var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

            try
            {
                if (underlying.IsEnum)
                {
                    return value is string name
                        ? Enum.Parse(underlying, name, ignoreCase: true)
                        : Enum.ToObject(underlying, value);
                }

                return Convert.ChangeType(value, underlying);
            }
            catch
            {
                MelonLogger.Warning($"[RecEmu] Cannot convert '{value}' to {underlying.Name}; leaving the member unchanged");
                return value;
            }
        }
    }
}