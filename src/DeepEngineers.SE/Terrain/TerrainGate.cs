using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using DeepEngineers.Terrain;
using DeepEngineers.Wire;
using Sandbox.Definitions;
using Sandbox.Engine.Voxels;
using Sandbox.Game.World;
using VRage;
using VRage.Game.Entity;
using VRage.Voxels;
using VRageMath;

namespace DeepEngineers.SE.Terrain
{
    /// <summary>
 /// The SE terrain gate (CONTRACT §1.2 row "SE terrain", §3.1 "Identity and registration", "Source manifest"): the
 /// SE half of <see cref="TerrainGate"/> (the decision table is in TerrainLogic.cs).
 /// <list type="bullet">
 /// <item><see cref="RegisterProviderOnce"/> (plugin Init, once per process): reads the private static
 /// <c>MyOctreeStorage.m_attributesById</c>
 /// id 45460 absent → <c>MyOctreeStorage.RegisterTypes(Assembly[])</c> (public static,
 /// VERIFIED; a second registration of a type throws <c>ArgumentException</c> from <c>Dictionary.Add</c>, hence
 /// this guard) in try/catch, then read again; registered iff the entry's <c>ProviderType</c> is
 /// <see cref="SnTerrainProvider"/>. Reading the field runs MyOctreeStorage's static constructor, which registers SE's
 /// own assemblies; <c>MySandboxGame.RegisterAssemblies</c> has set them before any plugin's Init
 /// </item>
 /// <item><see cref="MeasureOnce"/>: the source manifest, measured once per process (a directory listing).</item>
 /// <item><see cref="Evaluate"/>: conditions 1–6, game thread, in <c>BeforeStart</c>.</item>
 /// </list>
 /// </summary>
    internal static partial class TerrainGate
    {
        public static Result Last { get; private set; }
        public static string Condition { get; private set; }
        public static string Reason { get; private set; }
        public static bool ProviderRegistered { get; private set; }
        public static string ProviderError { get; private set; }
        /// <summary>The members condition 5 found missing (null when all resolved, "" before the first check).</summary>
        public static string MissingMembers { get; private set; }

        private static readonly object Lock = new object();
        private static SnSourceManifest _manifest;
        private static bool _registerAttempted;

        /// <summary>The manifest measured once per process (null before <see cref="MeasureOnce"/>).</summary>
        public static SnSourceManifest Manifest { get { lock (Lock) return _manifest; } }

        // ============================================================================================== registration

        public static void RegisterProviderOnce()
        {
            if (_registerAttempted) return;
            _registerAttempted = true;
            try
            {
                FieldInfo f = typeof(MyOctreeStorage).GetField("m_attributesById", BindingFlags.NonPublic | BindingFlags.Static);
                var byId = f != null ? f.GetValue(null) as Dictionary<int, MyStorageDataProviderAttribute> : null;
                if (byId == null) { ProviderFail("MyOctreeStorage.m_attributesById not found (Dictionary<int, MyStorageDataProviderAttribute>)"); return; }
                MyStorageDataProviderAttribute attr;
                if (!byId.TryGetValue(SnProviderId, out attr))
                {
                    try { MyOctreeStorage.RegisterTypes(new[] { typeof(SnTerrainProvider).Assembly }); }
                    catch (ReflectionTypeLoadException e)
                    {
                        string first = e.LoaderExceptions != null && e.LoaderExceptions.Length > 0 && e.LoaderExceptions[0] != null ? e.LoaderExceptions[0].Message : "";
                        ProviderFail("RegisterTypes ReflectionTypeLoadException: " + e.Message + " " + first);
                        return;
                    }
                    catch (Exception e) { ProviderFail("RegisterTypes " + e.GetType().Name + ": " + e.Message); return; }
                    if (!byId.TryGetValue(SnProviderId, out attr)) { ProviderFail("id " + SnProviderId + " still absent after RegisterTypes"); return; }
                }
                if (attr.ProviderType != typeof(SnTerrainProvider))
                {
                    ProviderFail("id " + SnProviderId + " belongs to " + (attr.ProviderType != null ? attr.ProviderType.AssemblyQualifiedName : "null"));
                    return;
                }
                ProviderRegistered = true;
                ProviderError = null;
                DeLog.Info("DE.Terrain PROVIDER registered id=" + SnProviderId + " type=" + typeof(SnTerrainProvider).FullName +
                           " (MyOctreeStorage.RegisterTypes, once per process; the id never reaches a save: tiles are not saved)");
            }
            catch (Exception e)
            {
                ProviderFail(e.GetType().Name + ": " + e.Message);
            }
        }

        private static void ProviderFail(string reason)
        {
            ProviderRegistered = false;
            ProviderError = reason;
            DeLog.Info("DE.Terrain PROVIDER FAIL " + reason + " (the terrain gate refuses in every DE world of this process)");
        }

        // ================================================================================================= manifest

 /// <summary>The configured Subnautica folder (S2's config.json <c>subnauticaDir</c> is not read yet: env/Steam default).</summary>
        public static string SubnauticaDir { get { return SnSourceManifest.ResolveSubnauticaDir(null); } }

        /// <summary>
 /// The manifest, measured once per process. A measurement that failed (<c>Error</c> set: no folder, a listing
 /// exception) is kept for <c>status</c> but not cached: the next world load measures again. A completed listing,
 /// equal to the pins or not, is cached.
 /// </summary>
        public static SnSourceManifest MeasureOnce()
        {
            lock (Lock)
            {
                if (_manifest != null && _manifest.Error == null) return _manifest;
            }
            SnSourceManifest m = MeasureNow();
            lock (Lock)
            {
                if (_manifest == null || _manifest.Error != null) _manifest = m;
                return _manifest;
            }
        }

        /// <summary>A fresh measurement (<c>de.terrain gate</c>), logged as <c>DE.Terrain SOURCE</c>; not cached.</summary>
        public static SnSourceManifest MeasureNow()
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            SnSourceManifest m = SnSourceManifest.Measure(SubnauticaDir);
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            DeLog.Info(m.LogLine() + " measuredMs=" + ms.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            return m;
        }

        /// <summary>Compares the manifest with the pins (S4.M5: expected files + 1).</summary>
        public static bool ManifestMatches(SnSourceManifest m, out string field, out string found, out string expected)
        {
            if (m == null) { field = "manifest"; found = "not measured"; expected = "measured"; return false; }
            return m.Matches(S4Mutants.WrongManifest ? 1 : 0, out field, out found, out expected);
        }

        // ================================================================================================= members

 /// <summary>Condition 5: the SE members the tiles need, looked up with explicit parameter types (null = all resolved).</summary>
        public static string CheckMembers()
        {
            var missing = new List<string>();
            try
            {
                if (typeof(Sandbox.Game.World.MyWorldGenerator).GetMethod("AddVoxelMap", BindingFlags.Public | BindingFlags.Static, null,
                        new[] { typeof(string), typeof(MyStorageBase), typeof(MatrixD), typeof(long), typeof(bool), typeof(bool) }, null) == null)
                    missing.Add("MyWorldGenerator.AddVoxelMap(string, MyStorageBase, MatrixD, long, bool, bool)");
                if (typeof(MyOctreeStorage).GetConstructor(new[] { typeof(IMyStorageDataProvider), typeof(Vector3I) }) == null)
                    missing.Add("MyOctreeStorage(IMyStorageDataProvider, Vector3I)");
                if (typeof(MyEntityIdentifier).GetMethod("ExistsById", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(long) }, null) == null)
                    missing.Add("MyEntityIdentifier.ExistsById(long)");
                if (typeof(MyDefinitionManager).GetMethod("GetVoxelMaterialDefinition", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null) == null)
                    missing.Add("MyDefinitionManager.GetVoxelMaterialDefinition(string)");
                if (typeof(MyDefinitionManager).GetMethod("GetDefaultVoxelMaterialDefinition", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null) == null)
                    missing.Add("MyDefinitionManager.GetDefaultVoxelMaterialDefinition()");
                PropertyInfo save = typeof(MyEntity).GetProperty("Save", BindingFlags.Public | BindingFlags.Instance);
                if (save == null || !save.CanWrite || save.PropertyType != typeof(bool)) missing.Add("MyEntity.Save (bool, settable)");
                if (typeof(MyStorageBase).GetProperty("DataProvider", BindingFlags.Public | BindingFlags.Instance) == null) missing.Add("MyStorageBase.DataProvider");
            }
            catch (Exception e) { missing.Add("lookup failed " + e.GetType().Name + ": " + e.Message); }
            MissingMembers = missing.Count == 0 ? null : string.Join("; ", missing.ToArray());
            return MissingMembers;
        }

        // ================================================================================================ evaluate

 /// <summary>CONTRACT §1.2 conditions 1–6 (game thread, BeforeStart). Logs SKIP/REFUSE; Ok is logged by the tiles.</summary>
        public static Result Evaluate(out string condition, out string reason)
        {
            var f = new Facts();
            string why;
            f.GlobalOk = ActivationGate.Peek(out why);
            f.GlobalWhy = why;
            f.PlanetCount = SiteBinding.PlanetCount;
            if (f.GlobalOk && f.PlanetCount == 1)
            {
                SnSourceManifest m = MeasureOnce();
                string mf, mfo, mex;
                f.ManifestOk = ManifestMatches(m, out mf, out mfo, out mex);
                f.ManifestField = mf; f.ManifestFound = mfo; f.ManifestExpected = mex;
                f.ProviderRegistered = ProviderRegistered;
                f.ProviderError = ProviderError ?? (ProviderRegistered ? null : "provider registration not attempted");
                if (f.ManifestOk && f.ProviderRegistered) f.MissingMembers = CheckMembers();
                if (f.ManifestOk && f.ProviderRegistered && f.MissingMembers == null && !S4Mutants.RandomIds) f.IdTaken = FirstTakenId();
            }
            Decision d = Decide(f);
            Record(d.Result, d.Condition, d.Reason);
            condition = d.Condition;
            reason = d.Reason;
            if (d.Line != null) DeLog.Info(d.Line + " (siteVersion=" + SiteV1.SiteVersion + " worldEpoch=" + Phases.WorldEpoch + ")");
            return d.Result;
        }

        /// <summary>Sets the last decision (the tiles' condition 7 calls it too).</summary>
        internal static void Record(Result r, string condition, string reason)
        {
            Last = r;
            Condition = condition;
            Reason = reason;
        }

        /// <summary>World unload.</summary>
        internal static void ResetSession()
        {
            Record(Result.NotEvaluated, null, null);
        }

        /// <summary>Condition 6: the first of the 34 ids <c>MyEntityIdentifier.ExistsById</c> finds taken (with its entity), or null.</summary>
        private static string FirstTakenId()
        {
            foreach (int[] t in SiteV1.TilesCreated())
            {
                long id = SiteV1.TileEntityId(t[0], t[1], t[2]);
                if (!MyEntityIdentifier.ExistsById(id)) continue;
                var sb = new StringBuilder();
                sb.Append("tile ").Append(t[0]).Append(',').Append(t[1]).Append(',').Append(t[2]).Append(" id=").Append(TerrainLattice.IdHex(id)).Append(" taken by ");
                try
                {
                    var e = MyEntityIdentifier.GetEntityById(id);
                    sb.Append(e != null ? e.GetType().Name + " '" + e.DisplayName + "'" : "an unregistered entity");
                }
                catch (Exception) { sb.Append("?"); }
                return sb.ToString();
            }
            return null;
        }
    }
}
