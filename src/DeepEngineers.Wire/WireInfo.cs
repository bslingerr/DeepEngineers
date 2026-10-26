using System;
using System.Runtime.CompilerServices;
using DeepEngineers.Wire.Native;

[assembly: InternalsVisibleTo("DeepEngineers.Wire.Tests")]

namespace DeepEngineers.Wire
{
    /// <summary>
 /// Identity of the bridge protocol (docs/CONTRACT.md §6). Both plugins log Describe at load.
 /// </summary>
    public static class WireInfo
    {
        public const string Name = "DeepEngineers.Wire";

        /// <summary>`wireVersion` (WIRE_VERSION in tools/worldmap/wire_ref.py).</summary>
        public const uint WireVersion = 1;

        /// <summary>Kept for the S0 skeleton's log line; same value as WireVersion.</summary>
        [Obsolete("Use WireVersion")]
        public const int ProtocolVersion = (int)WireVersion;

        /// <summary>`siteVersion` (SITE_VERSION in tools/worldmap/site_v1.py).</summary>
        public const uint SiteVersion = 1;

        /// <summary>`damageScaleVersion` (CONTRACT §4.1).</summary>
        public const uint DamageScaleVersion = 1;

        public const ushort LayoutMajor = Layout.Major;
        public const ushort LayoutMinor = Layout.Minor;

        /// <summary>The four bytes `D E E P` read as a little-endian u32.</summary>
        public const uint Magic = 0x50454544;

        public const uint ByteOrderMark = 0x01020304;

        /// <summary>Section name; the `_v1` follows layoutMajor.</summary>
        public const string SectionName = @"Local\DeepEngineers_v1";
        public const long SectionSize = Layout.SectionSize;

        /// <summary>Held by the SN plugin while in bridge mode.</summary>
        public const string MutexSn = @"Local\DeepEngineers_v1_sn";
        /// <summary>Held by the SE plugin while linked.</summary>
        public const string MutexSe = @"Local\DeepEngineers_v1_se";
        /// <summary>Named object the launcher holds until ReadyIdle.</summary>
        public const string LauncherObject = @"Local\DeepEngineers_v1_launcher";
        /// <summary>Environment variable bridge token: `DEEPENGINEERS_BRIDGE=1`.</summary>
        public const string BridgeEnvVar = "DEEPENGINEERS_BRIDGE";

        /// <summary>The section's security descriptor (CONTRACT §6) for a user SID string.</summary>
        public static string SectionSddl(string userSid)
        {
            if (string.IsNullOrEmpty(userSid)) throw new ArgumentException("userSid");
            return "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;" + userSid + ")S:(ML;;NW;;;ME)";
        }

        /// <summary>The section's security descriptor for the current user.</summary>
        public static string SectionSddlForCurrentUser()
        {
            return SectionSddl(Win32.CurrentUserSid());
        }

        public static string Describe()
        {
            string hash;
            try { hash = LayoutHash.Format(LayoutHash.Compiled); }
            catch (Exception e) { hash = "error(" + e.GetType().Name + ")"; }
            return Name + " wire v" + WireVersion + " layout " + LayoutMajor + "." + LayoutMinor + " hash " + hash +
                   " site v" + SiteVersion + " damageScale v" + DamageScaleVersion;
        }
    }

    /// <summary>Bridge token check (CONTRACT §6): Subnautica enters bridge mode only when one of the two tokens is present.</summary>
    public static class BridgeToken
    {
        /// <summary>True when `DEEPENGINEERS_BRIDGE=1` is in the environment or `Local\DeepEngineers_v1_launcher` exists.</summary>
        public static bool IsPresent(out string which)
        {
            string env = null;
            try { env = Environment.GetEnvironmentVariable(WireInfo.BridgeEnvVar); } catch (Exception) { }
            if (env == "1") { which = "env " + WireInfo.BridgeEnvVar + "=1"; return true; }
            int err;
            NamedObjectState s = Win32.QueryNamedObject(WireInfo.LauncherObject, out err);
            if (s == NamedObjectState.Present) { which = "named object " + WireInfo.LauncherObject; return true; }
            which = s == NamedObjectState.Error ? "none (named-object query error " + err + ")" : "none";
            return false;
        }
    }
}
