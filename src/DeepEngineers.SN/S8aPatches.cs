using System;
using System.Collections.Generic;
using System.Reflection;
using DeepEngineers.Wire;
using HarmonyLib;
using UWE;
using WorldStreaming;
using WindowC = DeepEngineers.Wire.S8aConventions.Window;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The Subnautica-side Harmony patches of S8a (CONTRACT §3.3 "Subnautica members S8a uses"; installed once by the
 /// first <see cref="S8aTerrain"/>, never removed in a session; a missing or reshaped target logs
 /// <c>DE.S8a MISSING &lt;member&gt;</c> once and only its feature stays off). Stage A (S8a-1, the drill layer):
 /// <list type="bullet">
 /// <item><c>BatchOctreesStreamer.GetPath(Int3 batchId)</c> → <c>string</c>
 /// <b>Postfix</b>: the store's validated version of that
 /// batch when the active host's store view names one (<see cref="S8aTerrain.ServedPath"/>), else the original result.
 /// S8a.M4 withholds the store's answer (the install is served again).</item>
 /// <item><c>BatchOctrees.LoadOctrees</c> → <c>bool</c>
 /// <b>Postfix</b>: records per batch id the path the batch just loaded
 /// (the GetPath postfix's last answer for that id) so the host starts the cell reloads only once the served file is
 /// in, never on <c>IsLoaded</c> (true for Loaded and QueuedForUnloading alike).</item>
 /// <item>Stage B (S8a-2) adds the <c>LoadOctrees</c> prefix for window ids and the <c>BatchOctreesStreamer</c>
 /// constructor prefix that widens the centre clamp (CONTRACT "window.loader", "window.center"); their C7 flags
 /// (<c>LoaderPatched</c>, <c>CenterWidened</c>) stay 0 until then.</item>
 /// </list>
 /// </summary>
    internal static class S8aPatches
    {
        public const string HarmonyId = "deepengineers.sn.s8a";

        private static Harmony _harmony;
        private static bool _installed;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Int3Key, string> _lastPath = new System.Collections.Concurrent.ConcurrentDictionary<Int3Key, string>();

        public static bool GetPathPatched { get; private set; }
        public static bool LoaderPatched { get; private set; }
        public static bool LoaderPostfixPatched { get; private set; }
        public static bool CenterWidened { get; private set; }
        public static readonly List<string> Missing = new List<string>();
        public static long Served, PassThrough, M4Withheld, Errors, Loads;

        /// <summary>The path the GetPath postfix last answered for a batch id (served or original), or null.</summary>
        public static string LastPathOf(Int3Key id)
        {
            string p;
            return _lastPath.TryGetValue(id, out p) ? p : null;
        }

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            try { _harmony = new Harmony(HarmonyId); }
            catch (Exception e)
            {
                DeLog.Error("DE.S8a MISSING Harmony instance: " + e.GetType().Name + ": " + e.Message + " (no GetPath postfix: the store is never served)");
                return;
            }
            GetPathPatched = Patch(typeof(BatchOctreesStreamer), "GetPath", new[] { typeof(Int3) }, typeof(string), false, nameof(Postfix_GetPath),
                                   "BatchOctreesStreamer.GetPath(Int3)", postfix: true);
            LoaderPostfixPatched = Patch(typeof(BatchOctrees), "LoadOctrees", Type.EmptyTypes, typeof(bool), false, nameof(Postfix_LoadOctrees),
                                         "BatchOctrees.LoadOctrees()", postfix: true);
            // S8a-2 (CONTRACT "window.loader", "window.center"): window ids load all 125 octrees from the store; the full-detail
 // streamer's centre clamp widens to the window universe when the window is on this side (DEEPENGINEERS_S8A=on and
 // DEEPENGINEERS_S8A_WINDOW not off: read at install, the streamer is built at world load before the feed is seen)
            WindowEnabled = EnvOn("DEEPENGINEERS_S8A", false) && EnvOn("DEEPENGINEERS_S8A_WINDOW", true);
            LoaderPatched = Patch(typeof(BatchOctrees), "LoadOctrees", Type.EmptyTypes, typeof(bool), false, nameof(Prefix_LoadOctrees), "BatchOctrees.LoadOctrees() prefix", postfix: false);
            CenterWidened = WindowEnabled && PatchCtor(typeof(BatchOctreesStreamer), new[] { typeof(IThread), typeof(Int3.Bounds), typeof(int), typeof(int), typeof(int), typeof(int), typeof(string), typeof(BatchOctreesStreamer.Settings) },
                                                       nameof(Prefix_StreamerCtor), "BatchOctreesStreamer(IThread, Int3.Bounds, int, int, int, int, string, Settings)");
            // Stage E: Subnautica's large resource deposits inside the Subnautica map, reported for SE's ore pockets (S8aCraterReports)
            DrillablePatched = Patch(typeof(Drillable), "Start", Type.EmptyTypes, typeof(void), false, nameof(Postfix_DrillableStart), "Drillable.Start()", postfix: true);
            // planet v2 R8: this mod's deposits are never damaged by Subnautica's drills (S8a.NO.immune; S8a.M25 lets the drill through)
            DrillImmunePatched = Patch(typeof(Drillable), "OnDrill", new[] { typeof(UnityEngine.Vector3), typeof(Exosuit), typeof(UnityEngine.GameObject).MakeByRefType() }, typeof(void), false,
                                       nameof(Prefix_DrillableOnDrill), "Drillable.OnDrill(Vector3, Exosuit, out GameObject)", postfix: false);
            // OnEntityStart)
            EntityStartPatched = Patch(typeof(LargeWorldEntity), "Start", Type.EmptyTypes, typeof(void), false, nameof(Postfix_EntityStart), "LargeWorldEntity.Start()", postfix: true);
            // a test run: Subnautica's own cell registration (entities its cells place or move), the flora registry's second path
            CellRegisterPatched = Patch(typeof(CellManager), "RegisterEntity", new[] { typeof(LargeWorldEntity) }, typeof(bool), false, nameof(Postfix_CellRegister),
                                        "CellManager.RegisterEntity(LargeWorldEntity)", postfix: true);
            // planet v2, ores as voxels (ext s8a terrain 6, format convert.orelook): this mod's ore block types in the palette's null slots
            if (EnvOn("DEEPENGINEERS_S8A", false)) S8aOrePalette.Install(_harmony);
            DeLog.Info("DE.S8a PATCH " + HarmonyId + " drillable=" + (DrillablePatched ? "installed (postfix: crater deposits reported)" : "MISSING") +
                       " entityStart=" + (EntityStartPatched ? "installed (postfix: late props over carves)" : "MISSING") + " getPath=" + (GetPathPatched ? "installed (postfix: the store's validated version when the view names one)" : "MISSING") +
                       " loadOctrees=" + (LoaderPostfixPatched ? "installed (postfix: the loaded path per batch)" : "MISSING (IsLoaded edge detection stands)") +
                       " loader-prefix=" + (LoaderPatched ? "installed (window ids: all 125 octrees from the store)" : "MISSING") +
                       " center=" + (CenterWidened ? "widened to [" + WindowC.UniverseMin + ", " + WindowC.UniverseMax + "] (window on)" : WindowEnabled ? "MISSING" : "crater (window off on this side)") +
                       (Missing.Count > 0 ? " missing=" + string.Join(";", Missing.ToArray()) : ""));
        }

        public static bool DrillablePatched { get; private set; }
        public static bool EntityStartPatched { get; private set; }
        public static bool CellRegisterPatched { get; private set; }
        public static bool DrillImmunePatched { get; private set; }
        public static long DrillsRefused;

        /// <summary>
 /// <c>Drillable.OnDrill(Vector3, Exosuit, out GameObject)</c>
 /// <b>Prefix</b>: on this mod's deposits
 /// (S8aOreDeposits.IsOurs) the hit object is the deposit and the original never runs (SE's drill is the only tool); S8a.M25 lets it through.
 /// </summary>
        private static bool Prefix_DrillableOnDrill(Drillable __instance, ref UnityEngine.GameObject hitObject)
        {
            try
            {
                if (__instance != null && !S8aMutants.M25 && S8aOreDeposits.IsOurs(__instance.gameObject))
                {
                    hitObject = __instance.gameObject;
                    DrillsRefused++;
                    return false;
                }
            }
            catch (Exception) { Errors++; }
            return true;
        }

        /// <summary>
 /// <c>LargeWorldEntity.Start</c>
 /// <b>Postfix</b>: <see cref="S8aFlora.OnEntityStart"/> (a cheap box test; only inside carved boxes).
 /// </summary>
        private static void Postfix_EntityStart(LargeWorldEntity __instance)
        {
            try { S8aFlora.OnEntityStart(__instance); }
            catch (Exception) { Errors++; }
        }

        /// <summary>
 /// a test run: <c>CellManager.RegisterEntity(LargeWorldEntity lwe)</c>
 /// <b>Postfix</b>: the flora registry's "cell" path.
 /// </summary>
        private static void Postfix_CellRegister(LargeWorldEntity lwe)
        {
            try { S8aFlora.Register(lwe, "cell"); }
            catch (Exception) { Errors++; }
        }

        /// <summary>
 /// Stage E: <c>Drillable.Start</c>
 /// <b>Postfix</b>: <see cref="S8aCraterReports.Report"/> (inside the Subnautica map only).
 /// </summary>
        private static void Postfix_DrillableStart(Drillable __instance)
        {
            try { if (__instance != null && !S8aOreDeposits.IsOurs(__instance.gameObject)) S8aCraterReports.Report(__instance.gameObject); }
            catch (Exception) { Errors++; }
        }

        /// <summary>The window is on for this Subnautica (the environment the launcher gives it; the SE header's WindowOn flag decides the serving).</summary>
        public static bool WindowEnabled { get; private set; }
        public static long WindowLoads, WindowLoadFailures, CtorWidened;

        private static bool EnvOn(string name, bool def)
        {
            string v = null;
            try { v = Environment.GetEnvironmentVariable(name); } catch (Exception) { }
            if (string.IsNullOrEmpty(v)) return def;
            v = v.Trim().ToLowerInvariant();
            if (v == "on" || v == "1" || v == "true" || v == "yes") return true;
            if (v == "off" || v == "0" || v == "false" || v == "no") return false;
            return def;
        }

        private static bool PatchCtor(Type type, Type[] args, string method, string label)
        {
            try
            {
                System.Reflection.ConstructorInfo c = AccessTools.Constructor(type, args);
                if (c == null) { Missing.Add(label); DeLog.Warn("DE.S8a MISSING " + label + " (feature off)"); return false; }
                _harmony.Patch(c, prefix: new HarmonyMethod(typeof(S8aPatches).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)));
                return true;
            }
            catch (Exception e)
            {
                Missing.Add(label);
                DeLog.Warn("DE.S8a MISSING " + label + ": " + e.GetType().Name + ": " + e.Message + " (feature off)");
                return false;
            }
        }

        // ------------------------------------------------------------------------------------------ the window loader (I/O thread)

 /// <summary>
 /// CONTRACT "window.loader"
 /// a window id (bx or bz outside the crater box) reads all 125 octrees
 /// in file order without the bounds test (the install's bounds would skip them); a missing or bad file answers false
 /// (the caller clears the octrees: water). Crater ids run the original.
 /// </summary>
        private static bool Prefix_LoadOctrees(BatchOctrees __instance, BatchOctreesStreamer ___streamer, SplitNativeArrayPool<byte> ___allocator, Array3<Octree> ___octrees, ref bool __result)
        {
            try
            {
                Int3 id = __instance.id;
                if (!S8aWindowPlan.IsWindowId(id.x, id.y, id.z)) return true;
                string path = ___streamer.GetPath(id);
                if (path == null || !System.IO.File.Exists(path)) { __result = false; return false; }
                try
                {
                    using (var reader = new PooledBinaryReader(System.IO.File.OpenRead(path)))
                    {
                        if (reader.ReadInt32() < S8aConventions.SnWorld.FileVersion) { WindowLoadFailures++; __result = false; return false; }
                        foreach (Int3 item in Int3.Range(S8aConventions.SnWorld.OctreesPerBatch))
                        {
                            Octree octree = ___octrees.Get(item.x, item.y, item.z);
                            octree.Read(reader, id, ___allocator);
                            octree.UnloadChildren(___streamer.minLod, ___allocator);
                        }
                    }
                    WindowLoads++;
                    __result = true;
                }
                catch (Exception e)
                {
                    WindowLoadFailures++;
                    DeLog.WarnOnce("s8a.windowload", "DE.S8a window batch " + id + " not loaded from " + path + ": " + e.GetType().Name + ": " + e.Message + " (water)");
                    __result = false;
                }
                return false;
            }
            catch (Exception) { Errors++; return true; }
        }

        /// <summary>CONTRACT "window.center": the full-detail streamer's centre clamp spans the window universe (the public Int3 fields of the sealed Settings; octreeBounds untouched).</summary>
        private static void Prefix_StreamerCtor(int minLod, BatchOctreesStreamer.Settings settings)
        {
            try
            {
                if (minLod != 0 || settings == null) return;
                settings.centerMin = new Int3(WindowC.UniverseMin, 0, WindowC.UniverseMin);
                settings.centerMax = new Int3(WindowC.UniverseMax, 20, WindowC.UniverseMax);
                CtorWidened++;
                DeLog.Info("DE.S8a window: full-detail streamer centre clamp widened to " + settings.centerMin + " .. " + settings.centerMax);
            }
            catch (Exception) { Errors++; }
        }

        private static bool Patch(Type type, string name, Type[] args, Type returnType, bool isStatic, string method, string label, bool postfix)
        {
            try
            {
                MethodInfo m = AccessTools.Method(type, name, args);
                if (m == null)
                {
                    Missing.Add(label);
                    DeLog.Warn("DE.S8a MISSING " + label + " (feature off)");
                    return false;
                }
                if (m.IsStatic != isStatic || m.ReturnType != returnType)
                {
                    Missing.Add(label);
                    DeLog.Warn("DE.S8a MISSING " + label + " shape: static=" + m.IsStatic + " returns " + m.ReturnType.Name + " (feature off)");
                    return false;
                }
                var hm = new HarmonyMethod(typeof(S8aPatches).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic));
                if (postfix) _harmony.Patch(m, postfix: hm);
                else _harmony.Patch(m, prefix: hm);
                return true;
            }
            catch (Exception e)
            {
                Missing.Add(label);
                DeLog.Warn("DE.S8a MISSING " + label + ": " + e.GetType().Name + ": " + e.Message + " (feature off)");
                return false;
            }
        }

        // ------------------------------------------------------------------------------------------ GetPath (I/O thread)

        private static void Postfix_GetPath(Int3 batchId, ref string __result)
        {
            try
            {
                //  the install's own path per batch (the flora rule reads the seabed before any carve there)
                if (__result != null) S8aVoxelGround.InstallPaths[S8aVoxelGround.Key(batchId.x, batchId.y, batchId.z)] = __result;
                string p = S8aTerrain.ServedPath(batchId.x, batchId.y, batchId.z);
                if (p == null) PassThrough++;
                else if (S8aMutants.M4) M4Withheld++;
                else { __result = p; Served++; }
                _lastPath[new Int3Key(batchId.x, batchId.y, batchId.z)] = __result;
            }
            catch (Exception)
            {
                Errors++;   // a patch body never throws into the game: the original path stands
            }
        }

        // ------------------------------------------------------------------------------------------ LoadOctrees (I/O thread)

        private static void Postfix_LoadOctrees(BatchOctrees __instance, bool __result)
        {
            try
            {
                Loads++;
                Int3 id = __instance.id;
                S8aTerrain.OnBatchLoaded(id.x, id.y, id.z, __result);
            }
            catch (Exception) { Errors++; }
        }
    }
}
