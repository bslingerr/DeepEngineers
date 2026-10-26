using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DeepEngineers.Wire.Native;
using HarmonyLib;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Weapons;
using VRage.Game;
using VRage.Voxels;
using VRageMath;

namespace DeepEngineers.SE.Terrain
{
    /// <summary>
 /// Edit guards (CONTRACT §3.1 "Edit guards", ROADMAP S4 S4.N2): Harmony prefixes that skip the original when the
 /// target storage's <c>DataProvider is SnTerrainProvider</c>, set the out values and <c>__result</c> as the contract
 /// says, count the attempt per path (Interlocked; prefixes run on worker threads too) and log
 /// <c>DE.Terrain EditBlocked &lt;path&gt; n=&lt;count&gt;</c> at most once per second per path. Each target is resolved by exact
 /// signature and logged <c>DE.Terrain GUARD &lt;path&gt; ok|MISSING</c>; a missing guard leaves that path unguarded and
 /// never refuses the terrain. Prefix parameters are positional (<c>__0</c>, …) so a renamed parameter cannot break
 /// the binding. Targets:
 /// <list type="bullet">
 /// <item>cutout: <c>MyVoxelGenerator.CutOutShapeWithProperties(MyVoxelBase, MyShape, out float, out
 /// MyVoxelMaterialDefinition, Dictionary&lt;MyVoxelMaterialDefinition, int&gt;, bool, bool, bool, bool, bool)</c> static void
 /// → percent 0, material null, dictionary untouched (no ore);</item>
 /// <item>shipdrill: <c>MyShipMiningSystem.RequestCutOut(MyShipDrill, bool, bool, Vector3D, MyVoxelBase)</c> instance void
 /// → nothing queued (the mining system counts ore before its WriteRange);</item>
 /// <item>explosion: <c>MyVoxelGenerator.CutOutSphereFast(MyVoxelBase, ref Vector3D, float, out Vector3I, out Vector3I,
 /// bool)</c> static bool → <c>__result = false</c>, both out values zero;</item>
 /// <item>meteor: <c>MyVoxelGenerator.MakeCrater(MyVoxelBase, BoundingSphereD, Vector3, MyVoxelMaterialDefinition)</c> static
 /// void;</item>
 /// <item>voxelhand: <c>MyVoxelBase.UpdateVoxelShape(MyVoxelBase.OperationType, MyShape, byte)</c> private instance void
 /// and <c>MyVoxelGenerator.CutOutShape(MyVoxelBase,
 /// MyShape, bool)</c>, <c>FillInShape(MyVoxelBase, MyShape, byte)</c>, <c>PaintInShape(MyVoxelBase, MyShape,
 /// byte)</c>, <c>RevertShape(MyVoxelBase, MyShape)</c>, <c>ChangeMaterialsInShape(MyVoxelBase, MyShape, byte,
 /// bool[])</c>, static void;</item>
 /// <item>writerange: <c>MyStorageBase.WriteRange(MyStorageData, MyStorageDataTypeFlags, Vector3I, Vector3I, bool, bool)</c>
 /// instance void.</item>
 /// </list>
 /// S4.M9.&lt;path&gt; lets that path's edits through (the prefix answers "run the original"), but only in a throwaway
 /// copy: elsewhere the guard keeps
 /// blocking and logs <c>REFUSE S4.M9.&lt;path&gt;</c> once per path and world.
 /// <para>Storage-level entries not patched, by decomp: on a <c>MyOctreeStorage</c>
 /// with a provider, <c>DeleteRange</c> (private; the ModAPI <c>IMyStorage.DeleteRange</c>, <c>RevertShape</c>) removes
 /// edited leaves so the provider's data returns (<c>MyOctreeStorage.DeleteRangeInternal</c>), <c>Sweep</c> and
 /// <c>Reset</c> compact or reset to the provider, and <c>OverwriteAllMaterialsInternal</c> is empty
 /// none can make a tile differ from Subnautica's data. The generic sink
 /// <c>ExecuteOperationFast&lt;TVoxelOperator&gt;</c> (one JIT body per operator; the ModAPI
 /// <c>IMyStorage.ExecuteOperationFast&lt;T&gt;</c> reaches it directly) stays unguarded: only a mod can call it, and an
 /// edit would live in memory only (tiles are never saved).</para>
 /// </summary>
    internal static partial class TerrainGuards
    {
        private static readonly bool[] _installed = new bool[6];
        private static readonly string[] _detail = new string[6];
        private static int _m9Refused;                   // bit per path: REFUSE logged in this world

        public static bool Installed(int path) { return _installed[path]; }
        public static string Detail(int path) { return _detail[path]; }

        /// <summary>True when the voxel map's storage is one of our tiles' (thread-safe; never throws).</summary>
        public static bool IsOurs(MyVoxelBase v)
        {
            if (v == null) return false;
            try { return IsOurs(v.Storage as MyStorageBase); }
            catch (Exception) { return false; }
        }

        public static bool IsOurs(MyStorageBase s)
        {
            if (s == null) return false;
            try { return s.DataProvider is SnTerrainProvider; }
            catch (Exception) { return false; }
        }

        // ================================================================================================== install

 /// <summary>World load: the S4.M9 refusals are logged again once per path.</summary>
        public static void ResetSession()
        {
            System.Threading.Volatile.Write(ref _m9Refused, 0);
        }

        public static void Install(Harmony h)
        {
            Type gen = typeof(MyVoxelGenerator);
            Type vb = typeof(MyVoxelBase);
            Path(h, TerrainPaths.Cutout, new[]
            {
                T(gen, "CutOutShapeWithProperties", true, typeof(void), nameof(CutoutPrefix),
                  typeof(MyVoxelBase), typeof(MyShape), typeof(float).MakeByRefType(), typeof(MyVoxelMaterialDefinition).MakeByRefType(),
                  typeof(Dictionary<MyVoxelMaterialDefinition, int>), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool)),
            });
            Path(h, TerrainPaths.ShipDrill, new[]
            {
                T(typeof(MyShipMiningSystem), "RequestCutOut", false, typeof(void), nameof(ShipDrillPrefix),
                  typeof(MyShipDrill), typeof(bool), typeof(bool), typeof(Vector3D), typeof(MyVoxelBase)),
            });
            Path(h, TerrainPaths.Explosion, new[]
            {
                T(gen, "CutOutSphereFast", true, typeof(bool), nameof(ExplosionPrefix),
                  typeof(MyVoxelBase), typeof(Vector3D).MakeByRefType(), typeof(float), typeof(Vector3I).MakeByRefType(), typeof(Vector3I).MakeByRefType(), typeof(bool)),
            });
            Path(h, TerrainPaths.Meteor, new[]
            {
                T(gen, "MakeCrater", true, typeof(void), nameof(StaticPrefix),
                  typeof(MyVoxelBase), typeof(BoundingSphereD), typeof(Vector3), typeof(MyVoxelMaterialDefinition)),
            });
            Path(h, TerrainPaths.VoxelHand, new[]
            {
                T(vb, "UpdateVoxelShape", false, typeof(void), nameof(UpdateVoxelShapePrefix), typeof(MyVoxelBase.OperationType), typeof(MyShape), typeof(byte)),
                T(gen, "CutOutShape", true, typeof(void), nameof(VoxelHandStaticPrefix), typeof(MyVoxelBase), typeof(MyShape), typeof(bool)),
                T(gen, "FillInShape", true, typeof(void), nameof(VoxelHandStaticPrefix), typeof(MyVoxelBase), typeof(MyShape), typeof(byte)),
                T(gen, "PaintInShape", true, typeof(void), nameof(VoxelHandStaticPrefix), typeof(MyVoxelBase), typeof(MyShape), typeof(byte)),
                T(gen, "RevertShape", true, typeof(void), nameof(VoxelHandStaticPrefix), typeof(MyVoxelBase), typeof(MyShape)),
                T(gen, "ChangeMaterialsInShape", true, typeof(void), nameof(VoxelHandStaticPrefix), typeof(MyVoxelBase), typeof(MyShape), typeof(byte), typeof(bool[])),
            });
            Path(h, TerrainPaths.WriteRange, new[]
            {
                T(typeof(MyStorageBase), "WriteRange", false, typeof(void), nameof(WriteRangePrefix),
                  typeof(MyStorageData), typeof(MyStorageDataTypeFlags), typeof(Vector3I), typeof(Vector3I), typeof(bool), typeof(bool)),
            });
        }

        private struct Target
        {
            public Type Type;
            public string Name;
            public bool Static;
            public Type Ret;
            public string Prefix;
            public Type[] Args;
        }

        private static Target T(Type t, string name, bool isStatic, Type ret, string prefix, params Type[] args)
        {
            return new Target { Type = t, Name = name, Static = isStatic, Ret = ret, Prefix = prefix, Args = args };
        }

        private static void Path(Harmony h, int path, Target[] targets)
        {
            var ok = new List<string>();
            var missing = new List<string>();
            foreach (Target t in targets)
            {
                string label = t.Type.Name + "." + t.Name + "(" + string.Join(", ", t.Args.Select(a => a.Name).ToArray()) + ")";
                try
                {
                    MethodInfo m = AccessTools.Method(t.Type, t.Name, t.Args);
                    if (m == null) { missing.Add(label + " not found"); continue; }
                    if (m.IsStatic != t.Static || m.ReturnType != t.Ret) { missing.Add(label + " has another shape (static=" + m.IsStatic + " returns " + m.ReturnType.Name + ")"); continue; }
                    MethodInfo pre = typeof(TerrainGuards).GetMethod(t.Prefix, BindingFlags.Static | BindingFlags.NonPublic);
                    h.Patch(m, prefix: new HarmonyMethod(pre));
                    ok.Add(label);
                }
                catch (Exception e) { missing.Add(label + " patch failed " + e.GetType().Name + ": " + e.Message); }
            }
            _installed[path] = missing.Count == 0;
            _detail[path] = missing.Count == 0 ? string.Join("; ", ok.ToArray()) : "MISSING " + string.Join("; ", missing.ToArray()) + (ok.Count > 0 ? " (patched: " + string.Join("; ", ok.ToArray()) + ")" : "");
            DeLog.Info("DE.Terrain GUARD " + TerrainPaths.Names[path] + " " + (missing.Count == 0 ? "ok " : "") + _detail[path] +
                       (missing.Count == 0 ? "" : " (that path is unguarded: an edit of a tile would live in memory only; terrain not refused)"));
            Jsonl.WriteLine(S4Rows.Row("s4.guard", "path", TerrainPaths.Names[path], "installed", _installed[path], "targets", targets.Length,
                                       "patched", ok.Count, "detail", _detail[path]));
        }

        // ================================================================================================== prefixes
 // Positional parameters (__0 = first argument). Never throw: a throwing prefix would break the game's own edit.

 /// <summary>True when the edit must be skipped (our tile, guard not removed by S4.M9); counts and rate-limits the log.</summary>
        private static bool Block(int path, MyVoxelBase v, MyStorageBase s)
        {
            try
            {
                bool ours = v != null ? IsOurs(v) : IsOurs(s);
                if (!ours) return false;
                if (S8aOverlay.AllowsEdit(path, v, s)) return false; // S8a (CONTRACT "guard.lift"): while DrillOn every path passes on a tile; the mirror/restore writes always
                if (S4Mutants.GuardOff(path))
                {
                    string refuse = TerrainLattice.GuardOffRefusal(path, TerrainTiles.AutosaveMinutes);
                    if (refuse == null) return false;
                    int bit = 1 << path, cur;
                    while (((cur = System.Threading.Volatile.Read(ref _m9Refused)) & bit) == 0)
                        if (System.Threading.Interlocked.CompareExchange(ref _m9Refused, cur | bit, cur) == cur) { DeLog.Info("DE.Mutant " + refuse); break; }
                }
                long n;
                if (RecordBlocked(path, (long)Win32.TickCount64(), out n))
                {
                    var p = (v != null ? v.Storage as MyStorageBase : s);
                    var prov = p != null ? p.DataProvider as SnTerrainProvider : null;
                    DeLog.Info("DE.Terrain EditBlocked " + TerrainPaths.Names[path] + " n=" + n +
                               (prov != null ? " tile=" + prov.I + "," + prov.J + "," + prov.K : "") + " (at most one line per second per path)");
                    Jsonl.WriteLine(S4Rows.Row("s4.guard", "path", TerrainPaths.Names[path], "blocked", n, "tile", prov != null ? prov.I + "," + prov.J + "," + prov.K : null));
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        private static bool CutoutPrefix(MyVoxelBase __0, ref float __2, ref MyVoxelMaterialDefinition __3)
        {
            if (!Block(TerrainPaths.Cutout, __0, null)) return true;
            __2 = 0f;
            __3 = null;
            return false;
        }

        private static bool ShipDrillPrefix(MyVoxelBase __4)
        {
            return !Block(TerrainPaths.ShipDrill, __4, null);
        }

        private static bool ExplosionPrefix(MyVoxelBase __0, ref Vector3I __3, ref Vector3I __4, ref bool __result)
        {
            if (!Block(TerrainPaths.Explosion, __0, null)) return true;
            __3 = Vector3I.Zero;
            __4 = Vector3I.Zero;
            __result = false;
            return false;
        }

        private static bool StaticPrefix(MyVoxelBase __0)
        {
            return !Block(TerrainPaths.Meteor, __0, null);
        }

        private static bool UpdateVoxelShapePrefix(MyVoxelBase __instance)
        {
            return !Block(TerrainPaths.VoxelHand, __instance, null);
        }

        private static bool VoxelHandStaticPrefix(MyVoxelBase __0)
        {
            return !Block(TerrainPaths.VoxelHand, __0, null);
        }

        private static bool WriteRangePrefix(MyStorageBase __instance)
        {
            return !Block(TerrainPaths.WriteRange, null, __instance);
        }
    }

    /// <summary>The S4 Harmony patches (plugin Init, with the other patches): the fail-safe's save block and the edit guards.</summary>
    internal static class TerrainPatches
    {
        public static void Apply()
        {
            var h = new Harmony(HarmonyPatches.HarmonyId);
            try { TerrainFailSafe.InstallSaveBlock(h); }
            catch (Exception e) { DeLog.Info("DE.Terrain GUARD saveblock MISSING " + e.GetType().Name + ": " + e.Message); }
            try { TerrainGuards.Install(h); }
            catch (Exception e) { DeLog.Info("DE.Terrain GUARD install FAILED " + e.GetType().Name + ": " + e.Message + " (edits of the tiles unguarded; terrain not refused)"); }
        }
    }
}
