using System;
using System.Globalization;
using BepInEx;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The `DE.Hello` identification line (ROADMAP S0 SN plugin, S0.N5): plugin, wire, layout, site and damage-scale
 /// versions, BepInEx, Unity and the game version. Game version sources:
 /// <c>Application.version</c> is the player's bundle version ("1.22.83031" in globalgamemanagers);
 /// <c>SNUtils.GetPlasticChangeSetOfBuild</c> reads StreamingAssets\SNUnmanagedData\plastic_status.ignore
 /// ("83031"); <c>SNUtils.GetDateTimeOfBuild</c> reads StreamingAssets\__buildtime.txt. Never throws.
 /// </summary>
    internal static class HelloInfo
    {
        public static string Line()
        {
            var ci = CultureInfo.InvariantCulture;
            return "DE.Hello side=sn plugin=" + Plugin.PluginName + " " + Plugin.PluginVersion +
                   " build=" + BuildFlavor.Name + " flavor=" + BuildFlavor.Name + // S9-release-flavor
                   " wire=" + WireInfo.WireVersion.ToString(ci) +
                   " layout=" + WireInfo.LayoutMajor.ToString(ci) + "." + WireInfo.LayoutMinor.ToString(ci) +
                   " layoutHash=" + Safe(() => LayoutHash.Format(LayoutHash.Compiled)) +
                   " site=" + WireInfo.SiteVersion.ToString(ci) +
                   " siteHash=" + SiteV1.HashHex(SiteV1.SiteHash) +
                   " damageScale=" + WireInfo.DamageScaleVersion.ToString(ci) +
                   " bepinex=" + Safe(() => typeof(BaseUnityPlugin).Assembly.GetName().Version.ToString()) +
                   " unity=" + Safe(() => Application.unityVersion) +
                   " game=" + Safe(() => Application.version) +
                   " changeset=" + Safe(() => SNUtils.GetPlasticChangeSetOfBuild()) +
                   " buildTime=" + Safe(() => SNUtils.GetDateTimeOfBuild().ToString("yyyy-MM-ddTHH:mm:ss", ci)) +
                   " clr=" + Safe(() => Environment.Version.ToString()) +
                   " x64=" + (IntPtr.Size == 8 ? "True" : "False") +
                   " pid=" + Safe(() => Win32.CurrentProcessId().ToString(ci));
        }

        private static string Safe(Func<string> f)
        {
            try
            {
                string s = f();
                return string.IsNullOrEmpty(s) ? "unknown" : s.Replace(' ', '_');
            }
            catch (Exception e)
            {
                return "error(" + e.GetType().Name + ")";
            }
        }
    }
}
