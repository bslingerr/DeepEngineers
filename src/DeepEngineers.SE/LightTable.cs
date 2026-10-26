using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.Lights;
using Sandbox.Game.World;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.EntityComponents.Blocks;
using VRage.Game.Entity;
using VRage.Game.Utils;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S7 lights, the SE light table (CONTRACT §5.4 "C2 lights (S7; S7Lights)", <c>format light.row</c>): every C2 snapshot

 /// <see cref="LightSelect"/>): a point row per <c>LightOn</c> and a spot row per <c>ReflectorOn</c>
 ///.
 /// <list type="bullet">
 /// Lights</c>
 /// through the field <c>m_lightingLogic</c> of <c>MyLightingBlock</c> (protected,
 /// <c>:135</c>), <c>MySearchlight</c> (<c>:478</c>), <c>MyHeatVentBlock</c> (<c>:297</c>) and the entity component
 /// <c>MyLightingComponent</c> (<c>:145</c>); <c>MyBeacon.m_light</c> (<c>:104</c>); the characters'
 /// <c>MyRenderComponentCharacter.m_light</c> (<c>:112</c>; every online player's character, v3). One cached
 /// </item>
 /// <item>Indexed per mirrored grid (S5 set and de.s3 registrations) when it enters the set and again after
 /// <c>OnBlockAdded</c>/<c>OnBlockRemoved</c> (one dirty grid re-indexed per call); handlers removed when it leaves.</item>
 /// <item><c>ownerId</c> = the grid (or subgrid) when its GridAdd is written, or the C1 character, with <c>pos</c>/<c>dir</c>
 /// in its SE-local frame (<c>WorldMatrixNormalizedInv</c>); else 0 and SN-world through F and mapdir. <c>flags</c> =
 /// Spot/Shadows/On, Character, version nibble 1 (<see cref="S7LightRow"/>); <c>pad_3c</c> = falloff.</item>
 /// </list>
 /// <see cref="Fill"/> is called by the C2 writer on the update thread; ≤ 0.1 ms per call; allocation-free per call after warm-up; rows <c>s7.lights</c>
 /// at 1 Hz; <c>de.lights</c> (<see cref="InteriorCommands"/>). A failure disables the table (0 rows; logged once).
 /// </summary>
    internal static class S7Lights
    {
        public const int MaxRows = S7Conventions.LightLimits.MaxLights;
        public const double RadiusM = S7Conventions.LightLimits.RadiusM;
        public const ulong RowIntervalMs = 1000;
        public const int MaxCandidates = 1024;

        private const int KindLightingBlock = 0, KindSearchlight = 1, KindHeatVent = 2, KindComponent = 3, KindBeacon = 4, KindCharacter = 5, Kinds = 6;
        private static readonly string[] KindNames = { "MyLightingBlock.m_lightingLogic", "MySearchlight.m_lightingLogic", "MyHeatVentBlock.m_lightingLogic",
                                                       "MyLightingComponent.m_lightingLogic", "MyBeacon.m_light", "MyRenderComponentCharacter.m_light" };
        private static readonly FieldInfo[] _fields = new FieldInfo[Kinds];
        private static readonly bool[] _kindOff = new bool[Kinds];
        private static bool _fieldsResolved;

        /// </summary>
        private struct Owner
        {
            public MyLightingLogic Logic;
            public MyLight Light;
            public MyCubeBlock Block;
        }

        private sealed class GridLights
        {
            public long Id;
            public MyCubeGrid Grid;
            public readonly List<Owner> Owners = new List<Owner>();
            public bool Dirty = true;
            public Action<MySlimBlock> OnChanged;
            public bool Seen;
        }

        private struct Cand
        {
            public MyLight Light;
            public bool Spot, Character;
            public MyEntity Frame;
            public long OwnerId;
            public long S10Owner;   // S10 hook (part 3): the owning grid or character for the per-grid light cap, attached or not
 // S7-1 fix: the light's SE-world position and spot direction. A block light's MyLight.Position is
 // grid-local
 // so it is taken
 // through the block grid's WorldMatrix; a character's light (no parent) is world already.
            public Vector3D Pos;
            public Vector3 Dir;
        }

        private static readonly Dictionary<long, GridLights> Grids = new Dictionary<long, GridLights>();
        private static readonly List<long> _gone = new List<long>();
        private static readonly Cand[] _cands = new Cand[MaxCandidates];
        private static readonly double[] _d2 = new double[MaxCandidates];
        private static readonly int[] _order = new int[MaxRows];
        private static bool _disabled;
        private static string _disabledWhy;
        private static ulong _nextRowMs;

        public static int LastCount, LastShadowed;
        public static double LastMaxDistM;
        // S7-1 diagnostics (per fill): lights looked at, switched off (neither LightOn nor ReflectorOn), beyond the radius,

        private static int LightsSeen, LightsOff, LightsFar;
        private static double NearestD2;
        public static int LastLightsSeen, LastLightsOff, LastLightsFar;
        public static double LastNearestM = -1;
        public static uint LastFound;
        public static long Fills, Reindexes, OwnersIndexed, CandidatesDropped, Errors, OwnerErrors;
        public static double LastUs, MaxUs;

        /// <summary>
 /// The C2 writer's call: fills <paramref name="rows"/> (length ≥ 64) with this tick's rows, nearest first;
 /// <paramref name="count"/> rows written, <paramref name="found"/> the candidates within 60 m before the cap
 /// (<c>c2ext.lightsFound</c>). Never throws (0 rows when disabled or without a camera or site).
 /// </summary>
        public static void Fill(SeLight[] rows, out int count, out uint found)
        {
            count = 0;
            found = 0;
            if (_disabled || rows == null) return;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                FillCore(rows, out count, out found);
            }
            catch (Exception e)
            {
                Errors++;
                count = 0;
                found = 0;
                _disabled = true;
                _disabledWhy = e.GetType().Name + ": " + e.Message;
                DeLog.Info("DE.S7 lights OFF (" + _disabledWhy + "): no light rows from now on. " + e.StackTrace);
                UnhookAll();
            }
            double us = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
            LastUs = us;
            if (us > MaxUs) MaxUs = us;
            Fills++;
            LastCount = count;
            LastFound = found;
            ulong now = Win32.TickCount64();
            if (now >= _nextRowMs)
            {
                _nextRowMs = now + RowIntervalMs;
                Row(now);
            }
        }

        // S10 hook (part 3): every candidate sorted nearest first, then the per-grid cap (S10LightCap); 0 or less = the S7 rule
        private static readonly int[] _s10Order = new int[MaxCandidates];
        private static readonly long[] _s10Owner = new long[MaxCandidates];
        private static readonly Dictionary<long, int> _s10Used = new Dictionary<long, int>();

        private static int S10Select(int n, int max)
        {
            if (S10Se.LightsPerGrid <= 0) return LightSelect.Select(_d2, n, RadiusM, max, _order);
            int all = LightSelect.Select(_d2, n, RadiusM, Math.Min(n, _s10Order.Length), _s10Order);
            for (int i = 0; i < all; i++) _s10Owner[_s10Order[i]] = _cands[_s10Order[i]].S10Owner;
            int k = S10LightCap.Keep(_s10Order, all, _s10Owner, S10Se.LightsPerGrid, max, _order, _s10Used);
            int plain = Math.Min(all, max);
            if (k < plain) S10Se.LightsCapped += plain - k;
            return k;
        }

        private static void FillCore(SeLight[] rows, out int count, out uint found)
        {
            count = 0;
            found = 0;
            SiteMap site = SiteBinding.Map;
            MyCamera camera = MySector.MainCamera;
            if (site == null || camera == null) return;
            ResolveFields();
            Vector3D cam = camera.WorldMatrix.Translation;
            SyncGrids();

            // candidates within the radius (lights of mirrored grids and of characters)
            int n = 0;
            double r2 = RadiusM * RadiusM;
            LightsSeen = LightsOff = LightsFar = 0;
            NearestD2 = double.MaxValue;
            foreach (GridLights gl in Grids.Values)
            {
                MyCubeGrid g = gl.Grid;
                if (g == null || g.Closed) continue;
                BoundingBoxD box = g.PositionComp.WorldAABB;
                if (box.Distance(cam) > RadiusM) continue;
                MyEntity frame = Attached(gl.Id) ? g : null;
                foreach (Owner o in gl.Owners)
                {
                    if (o.Block != null && o.Block.Closed) continue;
                    MyCubeGrid local = o.Block != null && o.Block.CubeGrid != null ? o.Block.CubeGrid : g; // S7-1 fix: grid-local lights
                    try
                    {
                        if (o.Logic != null)
                        {
                            List<MyLight> lights = o.Logic.Lights;
                            if (lights == null) continue;
                            for (int i = 0; i < lights.Count; i++) n = Add(lights[i], frame, gl.Id, false, cam, r2, n, local);
                        }
                        else n = Add(o.Light, frame, gl.Id, false, cam, r2, n, local);
                    }
                    catch (Exception)
                    {

                        OwnerErrors++;
                        gl.Dirty = true;
                    }
                }
            }
            n = AddCharacters(cam, r2, n);
            found = (uint)n;
            LastLightsSeen = LightsSeen;
            LastLightsOff = LightsOff;
            LastLightsFar = LightsFar;
            LastNearestM = NearestD2 < double.MaxValue ? Math.Sqrt(NearestD2) : -1;
            int k = S10Select(n, Math.Min(MaxRows, rows.Length)); // S10 hook (part 3): the nearest first, at most S10Se.LightsPerGrid per grid
            int shadowed = 0;
            for (int j = 0; j < k; j++)
            {
                Cand c = _cands[_order[j]];
                rows[j] = MakeRow(site, c);
                if ((rows[j].flags & SeLightFlags.Shadows) != 0) shadowed++;
            }
            count = k;
            LastShadowed = shadowed;
            LastMaxDistM = k > 0 ? Math.Sqrt(_d2[_order[k - 1]]) : 0.0; // rows are nearest first
            for (int i = 0; i < n; i++) _cands[i] = default(Cand); // no references held between ticks
        }

        /// <summary>
 /// Adds the point and/or spot candidate of one light (a light can be both). <paramref name="localTo"/>: the grid a
 /// parented block light is local to (S7-1 fix; null for a character's world light).
 /// </summary>
        private static int Add(MyLight l, MyEntity frame, long ownerId, bool character, Vector3D cam, double r2, int n, MyCubeGrid localTo = null)
        {
            if (l == null) return n;
            LightsSeen++;
            if (!(l.LightOn || l.ReflectorOn)) { LightsOff++; return n; }
            Vector3D pos = l.Position;
            Vector3 dir = l.ReflectorDirection;
            if (localTo != null && !localTo.Closed && l.ParentID != uint.MaxValue)
            {
                MatrixD gw = localTo.WorldMatrix;
                pos = Vector3D.Transform(pos, ref gw);
                dir = (Vector3)Vector3D.TransformNormal((Vector3D)dir, ref gw);
            }
            double d2 = Vector3D.DistanceSquared(pos, cam);
            if (d2 < NearestD2) NearestD2 = d2;
            if (!(d2 <= r2)) { LightsFar++; return n; }
            if (l.LightOn)
            {
                if (n >= MaxCandidates) { CandidatesDropped++; return n; }
                _cands[n] = new Cand { Light = l, Spot = false, Character = character, Frame = frame, OwnerId = frame != null ? ownerId : 0, S10Owner = ownerId, Pos = pos, Dir = dir };
                _d2[n] = d2;
                n++;
            }
            if (l.ReflectorOn)
            {
                if (n >= MaxCandidates) { CandidatesDropped++; return n; }
                _cands[n] = new Cand { Light = l, Spot = true, Character = character, Frame = frame, OwnerId = frame != null ? ownerId : 0, S10Owner = ownerId, Pos = pos, Dir = dir };
                _d2[n] = d2;
                n++;
            }
            return n;
        }

        private static int AddCharacters(Vector3D cam, double r2, int n)
        {
            if (_kindOff[KindCharacter] || _fields[KindCharacter] == null) return n;
            MySession s = MySession.Static;
            if (s == null) return n;
            long c1Id = CharacterExport.CurrentCharacterId;
            if (s.Players != null)
            {
                foreach (MyPlayer p in s.Players.GetOnlinePlayers())
                {
                    MyCharacter c = p != null ? p.Character : null;
                    if (c == null || c.Closed || c.EntityId == c1Id) continue;
                    n = AddCharacter(c, false, cam, r2, n);
                }
            }
            MyCharacter local = s.LocalCharacter;
            if (local != null && !local.Closed) n = AddCharacter(local, local.EntityId == c1Id && GridMirrorProducer.S5Active, cam, r2, n);
            return n;
        }

        private static int AddCharacter(MyCharacter c, bool mirrored, Vector3D cam, double r2, int n)
        {
            var rc = c.Render as MyRenderComponentCharacter;
            if (rc == null) return n;
            var l = _fields[KindCharacter].GetValue(rc) as MyLight;
            if (mirrored) NoteSuitLight(l); // S7p-character (P10)
            return Add(l, mirrored ? c : null, c.EntityId, true, cam, r2, n);
        }

        // S7p-character (P10, "the light of my suit is absolutely blinding"): what SE sends for the C1 character's lamp
 // (VERIFIED MyRenderComponentCharacter.UpdateLightPower: ReflectorIntensity = REFLECTOR_INTENSITY 25 × power, Intensity =
 // POINT_LIGHT_INTENSITY 0.5 × power; InitLight: ReflectorRange 35, Range POINT_LIGHT_RANGE 1.08); Subnautica scales the
 // character rows per kind (S7pLightScale), these raw values are the reference in the s7.lights row and de.lights status
        public static float LastSuitSpotIntensity = -1f, LastSuitSpotRange = -1f, LastSuitPointIntensity = -1f, LastSuitPointRange = -1f;
        public static bool LastSuitOn;

        private static void NoteSuitLight(MyLight l)
        {
            if (l == null) { LastSuitOn = false; return; }
            LastSuitOn = l.ReflectorOn || l.LightOn;
            LastSuitSpotIntensity = l.ReflectorIntensity;
            LastSuitSpotRange = l.ReflectorRange;
            LastSuitPointIntensity = l.Intensity;
            LastSuitPointRange = l.Range;
        }

        private static SeLight MakeRow(SiteMap site, Cand c)
        {
            MyLight l = c.Light;
            Vector3D p = c.Pos;                                 // S7-1 fix: SE-world (a block light's grid-local position mapped)
            Vector3 dirW = c.Spot ? c.Dir : Vector3.Zero;
            var row = new SeLight();
            if (c.Frame != null)
            {
                MatrixD inv = c.Frame.PositionComp.WorldMatrixNormalizedInv;
                Vector3D lp = Vector3D.Transform(p, ref inv);
                Vector3D ld = Vector3D.TransformNormal((Vector3D)dirW, ref inv);
                row.ownerId = (ulong)c.OwnerId;
                row.pos = new F32x3((float)lp.X, (float)lp.Y, (float)lp.Z);
                row.dir = new F32x3((float)ld.X, (float)ld.Y, (float)ld.Z);
            }
            else
            {
                var pv = new Vec3(p.X, p.Y, p.Z);
                Vec3 q = site.SeToSn(pv);
                Vec3 d = c.Spot ? site.DirSeToSn(pv, new Vec3(dirW.X, dirW.Y, dirW.Z)) : Vec3.Zero;
                row.ownerId = 0;
                row.pos = new F32x3((float)q.X, (float)q.Y, (float)q.Z);
                row.dir = new F32x3((float)d.X, (float)d.Y, (float)d.Z);
            }
            Vector3 col = c.Spot ? l.ReflectorColor.ToVector3() : l.Color.ToVector3();
            row.colour = new F32x3(col.X, col.Y, col.Z);
            if (c.Spot)
            {
                row.range = l.ReflectorRange;
                row.intensity = l.ReflectorIntensity;
                row.coneDeg = l.ReflectorConeDegrees;
                row.flags = S7LightRow.Flags(true, l.CastShadows, true, c.Character);
                S7LightRow.SetFalloff(ref row, l.ReflectorFalloff);
            }
            else
            {
                row.range = l.Range;
                row.intensity = l.Intensity;
                row.coneDeg = 0f;
                row.flags = S7LightRow.Flags(false, false, true, c.Character);
                S7LightRow.SetFalloff(ref row, l.Falloff);
            }
            return row;
        }

        /// <summary>A grid's rows are attached once its GridAdd is written (Subnautica has its Entity); else world rows.</summary>
        private static bool Attached(long id)
        {
            GridMirrorProducer.MirroredGrid mg;
            return GridMirrorProducer.Grids.TryGetValue(id, out mg) && mg.GridAddIndex >= 0 && GridMirrorProducer.IsC4Done(mg.GridAddIndex);
        }

        private static void SyncGrids()
        {
            foreach (GridLights gl in Grids.Values) gl.Seen = false;
            foreach (GridMirrorProducer.MirroredGrid mg in GridMirrorProducer.Grids.Values)
            {
                MyCubeGrid g = mg.Grid;
                if (g == null || g.Closed || g.MarkedForClose) continue;
                if (mg.Projection) continue; // S7p-scene-above (P7): a projector's hologram has no lights
                GridLights gl;
                if (!Grids.TryGetValue(mg.Id, out gl) || !ReferenceEquals(gl.Grid, g))
                {
                    if (gl != null) Unhook(gl);
                    gl = new GridLights { Id = mg.Id, Grid = g };
                    GridLights captured = gl;
                    gl.OnChanged = b => { captured.Dirty = true; };
                    g.OnBlockAdded += gl.OnChanged;
                    g.OnBlockRemoved += gl.OnChanged;
                    Grids[mg.Id] = gl;
                }
                gl.Seen = true;
            }
            _gone.Clear();
            foreach (KeyValuePair<long, GridLights> kv in Grids) if (!kv.Value.Seen) _gone.Add(kv.Key);
            foreach (long id in _gone)
            {
                Unhook(Grids[id]);
                Grids.Remove(id);
            }
            // one dirty grid re-indexed per call (the hooks report edits; a new grid starts dirty)
            foreach (GridLights gl in Grids.Values)
            {
                if (!gl.Dirty) continue;
                Reindex(gl);
                break;
            }
        }

        private static void Reindex(GridLights gl)
        {
            gl.Dirty = false;
            gl.Owners.Clear();
            Reindexes++;
            foreach (MyCubeBlock b in gl.Grid.GetFatBlocks())
            {
                if (b == null || b.Closed) continue;
                MyLightingLogic logic = null;
                MyLight single = null;
                if (b is MyLightingBlock) logic = Logic(KindLightingBlock, b);
                else if (b is MySearchlight) logic = Logic(KindSearchlight, b);
                else if (b is MyHeatVentBlock) logic = Logic(KindHeatVent, b);
                else if (b is MyBeacon && !_kindOff[KindBeacon] && _fields[KindBeacon] != null) single = _fields[KindBeacon].GetValue(b) as MyLight;
                if (logic != null) gl.Owners.Add(new Owner { Logic = logic, Block = b });
                else if (single != null) gl.Owners.Add(new Owner { Light = single, Block = b });
                if (!_kindOff[KindComponent] && _fields[KindComponent] != null && b.Components != null)
                {
                    MyLightingComponent comp;
                    if (b.Components.TryGet<MyLightingComponent>(out comp) && comp != null)
                    {
                        var cl = _fields[KindComponent].GetValue(comp) as MyLightingLogic;
                        if (cl != null && !ReferenceEquals(cl, logic)) gl.Owners.Add(new Owner { Logic = cl, Block = b });
                    }
                }
            }
            OwnersIndexed += gl.Owners.Count;
        }

        private static MyLightingLogic Logic(int kind, MyCubeBlock b)
        {
            if (_kindOff[kind] || _fields[kind] == null) return null;
            return _fields[kind].GetValue(b) as MyLightingLogic;
        }

        private static void ResolveFields()
        {
            if (_fieldsResolved) return;
            _fieldsResolved = true;
            const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            Type[] owners = { typeof(MyLightingBlock), typeof(MySearchlight), typeof(MyHeatVentBlock), typeof(MyLightingComponent), typeof(MyBeacon), typeof(MyRenderComponentCharacter) };
            string[] names = { "m_lightingLogic", "m_lightingLogic", "m_lightingLogic", "m_lightingLogic", "m_light", "m_light" };
            Type[] want = { typeof(MyLightingLogic), typeof(MyLightingLogic), typeof(MyLightingLogic), typeof(MyLightingLogic), typeof(MyLight), typeof(MyLight) };
            var found = new List<string>();
            for (int k = 0; k < Kinds; k++)
            {
                FieldInfo f = null;
                try { f = owners[k].GetField(names[k], F); } catch (Exception) { }
                if (f == null || !want[k].IsAssignableFrom(f.FieldType))
                {
                    _kindOff[k] = true;
                    DeLog.Info("DE.S7 lights OFF missing " + KindNames[k] + " (that owner type gives no rows)");
                }
                else
                {
                    _fields[k] = f;
                    found.Add(KindNames[k]);
                }
            }
            DeLog.Info("DE.S7 lights owners: " + string.Join(", ", found.ToArray()) + "; " + MaxRows + " rows within " + RadiusM + " m of the camera, nearest first");
        }

        private static void Unhook(GridLights gl)
        {
            if (gl.Grid != null && gl.OnChanged != null)
            {
                try { gl.Grid.OnBlockAdded -= gl.OnChanged; gl.Grid.OnBlockRemoved -= gl.OnChanged; } catch (Exception) { }
            }
            gl.OnChanged = null;
            gl.Owners.Clear();
        }

        private static void UnhookAll()
        {
            foreach (GridLights gl in Grids.Values) Unhook(gl);
            Grids.Clear();
        }

        // ============================================================================================ rows and status

        private static void Row(ulong now)
        {
            int owners = 0;
            foreach (GridLights gl in Grids.Values) owners += gl.Owners.Count;
            var row = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }, { "ev", "s7.lights" }, { "tick", (long)FrameWriter.LastTick },
                { "found", (long)LastFound }, { "sent", (long)LastCount }, { "shadowed", (long)LastShadowed }, { "owners", (long)owners },
                { "grids", (long)Grids.Count }, { "usLast", Math.Round(LastUs, 2) }, { "usMax", Math.Round(MaxUs, 2) }, { "disabled", _disabled },
                { "maxDistM", Math.Round(LastMaxDistM, 3) },
                { "lightsSeen", (long)LastLightsSeen }, { "lightsOff", (long)LastLightsOff }, { "lightsFar", (long)LastLightsFar },
                { "nearestM", Math.Round(LastNearestM, 2) },
                // S7p-character (P10): the C1 character's lamp as SE sends it (raw; Subnautica applies the per-kind scale)
                { "suitOn", LastSuitOn }, { "suitSpotIntensity", Math.Round(LastSuitSpotIntensity, 3) }, { "suitSpotRange", Math.Round(LastSuitSpotRange, 3) },
                { "suitPointIntensity", Math.Round(LastSuitPointIntensity, 3) }, { "suitPointRange", Math.Round(LastSuitPointRange, 3) },
            };
            Jsonl.WriteLineBuffered(JsonText.Serialize(row));
        }

        /// <summary><c>de.lights {action: status}</c>.</summary>
        public static Dictionary<string, object> Status()
        {
            int owners = 0;
            foreach (GridLights gl in Grids.Values) owners += gl.Owners.Count;
            var kinds = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int k = 0; k < Kinds; k++) kinds[KindNames[k]] = _fieldsResolved ? (_kindOff[k] ? "missing" : "ok") : "unresolved";
            Row(Win32.TickCount64());
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "found", (long)LastFound }, { "sent", (long)LastCount }, { "shadowed", (long)LastShadowed }, { "owners", (long)owners }, { "grids", (long)Grids.Count },
                { "fills", Fills }, { "reindexes", Reindexes }, { "ownersIndexed", OwnersIndexed }, { "candidatesDropped", CandidatesDropped }, { "ownerErrors", OwnerErrors },
                { "usLast", Math.Round(LastUs, 2) }, { "usMax", Math.Round(MaxUs, 2) }, { "ownerTypes", kinds }, { "radiusM", RadiusM }, { "maxRows", (long)MaxRows },
                { "disabled", _disabled }, { "disabledWhy", _disabledWhy ?? "" }, { "errors", Errors },
                { "lightsSeen", (long)LastLightsSeen }, { "lightsOff", (long)LastLightsOff }, { "lightsFar", (long)LastLightsFar },
                { "nearestM", Math.Round(LastNearestM, 2) },
                { "suitOn", LastSuitOn }, { "suitSpotIntensity", Math.Round(LastSuitSpotIntensity, 3) }, { "suitSpotRange", Math.Round(LastSuitSpotRange, 3) }, // S7p-character (P10)
                { "note", Fills == 0 ? "no C2 write has asked for lights yet (the C2 writer's light overload is the effects builder's Link hook)" : "" },
            };
        }
    }
}
