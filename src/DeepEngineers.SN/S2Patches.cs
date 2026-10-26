using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The Subnautica-side Harmony patches of S2 (bridge mode only; installed once at bridge start, before the world
 /// scene's scripts are compiled, so that callers compiled later reach them; never removed in a session). Targets read
 /// in the decompiled Assembly-CSharp of build 83031, looked up with explicit parameter types; a missing or reshaped
 /// target logs `DE.Harmony MISSING` once and only its feature stays off.
 /// <list type="bullet">
 /// <item><c>LargeWorld.GetBiome(Vector3 wsPos)</c> (public instance string; VERIFIED
 /// floors into land blocks, then <c>GetBiome(Int3)</c>, whose biome-map index has a
 /// total-range check only: it wraps east of the tile and returns null north and south, probe-world-mapping §1.3).
 /// Prefix: outside the tile (<see cref="BiomeRules.OutsideTile"/>) the answer is "void" and the original is skipped
 /// (WaterBiomeManager's fog, VoidGhostLeviathansSpawner, Player.GetBiomeString all call this overload). The land
 /// origin is logged once at the first patched answer. Seeded failure S2.M5: the prefix lets the original run.</item>
 /// <item><c>GameInput.ClearInput(int)</c> (public static void; VERIFIED). Prefix inert
 /// unless seeded failure S2.M2 is on; then every ClearInput is skipped (the S1 silencer in S1Host and GhostPlayer
 /// included: "silencer off"). Installed at bridge start because the method is small enough for Mono to inline into
 /// callers compiled before a patch.</item>
 /// </list>
 /// </summary>
    internal static class S2Patches
    {
        public const string HarmonyId = "deepengineers.sn.s2";

        private static Harmony _harmony;
        private static bool _installed;

        public static bool BiomePatched { get; private set; }
        public static bool ClearInputPatched { get; private set; }
        public static readonly List<string> Missing = new List<string>();

        public static long BiomeVoidAnswers, BiomePassThrough, ClearInputSkipped, BiomeS8aAnswers;

        /// <summary>The mutant switches as read once per frame by S2Host (GetBiome may run thousands of times a frame).</summary>
        public static volatile bool BiomeMutant, SilencerMutant;
        private static bool _landLogged, _mutantLogged;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            try
            {
                _harmony = new Harmony(HarmonyId);
            }
            catch (Exception e)
            {
                DeLog.Error("DE.Harmony S2 FAILED creating the Harmony instance: " + e.GetType().Name + ": " + e.Message + " (no GetBiome patch, no S2.M2 switch)");
                return;
            }
            BiomePatched = Patch(typeof(LargeWorld), "GetBiome", new[] { typeof(Vector3) }, typeof(string), false, "Prefix_GetBiome", "LargeWorld.GetBiome(Vector3)");
            ClearInputPatched = Patch(typeof(GameInput), "ClearInput", new[] { typeof(int) }, typeof(void), true, "Prefix_ClearInput", "GameInput.ClearInput(int)");
            VoidLeviathansOn = S2Switches.VoidLeviathans;
            VoidSpawnerPatched = Patch(typeof(VoidGhostLeviathansSpawner), "UpdateSpawn", Type.EmptyTypes, typeof(void), false, "Prefix_UpdateSpawn",
                                       "VoidGhostLeviathansSpawner.UpdateSpawn()");
            DeLog.Info("DE.Harmony SN " + HarmonyId + " getBiome=" + (BiomePatched ? "installed (void outside the tile)" : "MISSING") +
                       " clearInputSwitch=" + (ClearInputPatched ? "installed (inert unless S2.M2)" : "MISSING") +
                       " voidSpawner=" + (VoidSpawnerPatched ? "installed (" + (VoidLeviathansOn ? S2Switches.VoidLeviathansVar + "=on: vanilla spawns" : "no ghost leviathan spawn outside the tile: OUTER VOID is empty water") + ")" : "MISSING") +
                       (Missing.Count > 0 ? " missing=" + string.Join(";", Missing.ToArray()) : ""));
        }

        // ------------------------------------------------------------------------------------------ void spawner

        public static bool VoidSpawnerPatched { get; private set; }
        public static bool VoidLeviathansOn;
        public static long VoidSpawnsSkipped;
        private static bool _voidSkipLogged;

        /// <summary>
 /// Review fix (finding 9): <c>VoidGhostLeviathansSpawner.UpdateSpawn</c> (private instance void, run
 /// every 2 s by <c>InvokeRepeating</c>; VERIFIED) is skipped while
 /// the player is outside the tile, where our GetBiome answer "void" would otherwise make it spawn ghost
 /// leviathans (ARCHITECTURE §4.2: OUTER VOID is empty water). Switch <c>DEEPENGINEERS_SN_VOID_LEVIATHANS=on</c>.
 /// </summary>
        private static bool Prefix_UpdateSpawn()
        {
            try
            {
                Player p = Player.main;
                if (p == null) return true;
                Vector3 pos = p.transform.position;
                if (!BiomeRules.SuppressVoidSpawner(VoidLeviathansOn, pos.x, pos.z)) return true;
                VoidSpawnsSkipped++;
                if (!_voidSkipLogged)
                {
                    _voidSkipLogged = true;
                    var ci = CultureInfo.InvariantCulture;
                    DeLog.Info("DE.Biome VoidGhostLeviathansSpawner update skipped outside the tile (player at " + pos.x.ToString("0.#", ci) + ", " +
                               pos.z.ToString("0.#", ci) + "): no ghost leviathan spawns in OUTER VOID (" + S2Switches.VoidLeviathansVar + "=on restores them)");
                }
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
 /// The S2.N6 direct query (<c>de.biome</c>, main thread): <c>LargeWorld.main.GetBiome(p)</c>, through the patch
 ///.
 /// </summary>
        public static string QueryBiome(Vector3 p, out bool hasWorld)
        {
            LargeWorld w = LargeWorld.main;
            hasWorld = w != null;
            return hasWorld ? w.GetBiome(p) : null;
        }

        private static bool Patch(Type type, string name, Type[] args, Type returnType, bool isStatic, string prefix, string label)
        {
            try
            {
                MethodInfo m = AccessTools.Method(type, name, args);
                if (m == null)
                {
                    Missing.Add(label);
                    DeLog.Warn("DE.Harmony MISSING " + label + " (feature off)");
                    return false;
                }
                if (m.IsStatic != isStatic || m.ReturnType != returnType)
                {
                    Missing.Add(label);
                    DeLog.Warn("DE.Harmony MISSING " + label + " shape: static=" + m.IsStatic + " returns " + m.ReturnType.Name + " (feature off)");
                    return false;
                }
                _harmony.Patch(m, new HarmonyMethod(typeof(S2Patches).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)));
                return true;
            }
            catch (Exception e)
            {
                Missing.Add(label);
                DeLog.Warn("DE.Harmony MISSING " + label + ": " + e.GetType().Name + ": " + e.Message + " (feature off)");
                return false;
            }
        }

        // ------------------------------------------------------------------------------------------ GetBiome

        private static bool Prefix_GetBiome(LargeWorld __instance, Vector3 wsPos, ref string __result)
        {
            try
            {
                if (!BiomeRules.OutsideTile(wsPos.x, wsPos.z)) return true;
                if (BiomeMutant)
                {
                    BiomePassThrough++;
                    if (!_mutantLogged) { _mutantLogged = true; DeLog.Warn("DE.Biome GetBiome patch OFF outside the tile (MUTANT S2.M5)"); }
                    return true;
                }
                // S8a (B4 hook, CONTRACT "biome.rule"): beyond the crater box the window's cameraBiome within BiomeRadiusM of the camera, else void
                string s8a = S8aTerrain.BiomeOutsideTile(wsPos);
                if (s8a != null) { __result = s8a; BiomeS8aAnswers++; return false; }
                BiomeVoidAnswers++;
                if (!_landLogged)
                {
                    _landLogged = true;
                    Vector3 o = __instance != null && __instance.land != null ? __instance.land.transform.position : new Vector3(float.NaN, float.NaN, float.NaN);
                    var ci = CultureInfo.InvariantCulture;
                    DeLog.Info("DE.Biome first answer outside the tile at (" + wsPos.x.ToString("0.#", ci) + ", " + wsPos.y.ToString("0.#", ci) + ", " +
                               wsPos.z.ToString("0.#", ci) + "): \"void\" (tile x,z in [-" + SiteHalf() + ", +" + SiteHalf() + "); land origin (" +
                               o.x.ToString("0.###", ci) + ", " + o.y.ToString("0.###", ci) + ", " + o.z.ToString("0.###", ci) + ") for the record)");
                }
                __result = BiomeRules.VoidBiome;
                return false;
            }
            catch (Exception)
            {
                return true; // a patch body never throws into the game
            }
        }

        private static string SiteHalf() { return DeepEngineers.Wire.SiteV1.SnTileHalf.ToString("0", CultureInfo.InvariantCulture); }

        // ------------------------------------------------------------------------------------------ ClearInput (S2.M2)

        private static bool Prefix_ClearInput()
        {
            if (!SilencerMutant) return true;
            ClearInputSkipped++;
            return false;
        }
    }
}
