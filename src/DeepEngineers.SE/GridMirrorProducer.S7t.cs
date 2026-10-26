using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.Utils;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S7t se-composite in the S5 producer (ROADMAP "### S7t Seamless transition" T3; CONTRACT §5.4 "S7t impostors";
 /// rules in Wire <see cref="S7tImpostorRules"/>): the mirror's vertical reach. Every
 /// <see cref="S7tImpostorRules.QueryEveryTicks"/> ticks, under the Site picture policy with the entity set allowed to
 /// add (C5 zone CORE / TILE VOID / OUTER VOID, the stream up), the top-most entities in a sphere above the camera
 /// give the candidates: hierarchy roots (<see cref="EntitySet.RootOf"/>) that are <c>MyCubeGrid</c>s of
 /// <c>GridSizeEnum</c> Large, not <c>IsPreview</c>, at most
 /// <see cref="S7tImpostorRules.MaxBlocks"/> blocks (<c>BlocksCount</c>), mirrorable (<see cref="EntitySet.Mirrorable"/>),
 /// not in the S5 entity set, inside the reach (<see cref="S7tImpostorRules.InReach"/> on the <c>WorldAABB</c>'s centre and
 /// half-diagonal, split along the planet's radial at the camera). The nearest
 /// <see cref="S7tImpostorRules.MaxAdmitsPerQuery"/> enter while fewer than <see cref="S7tImpostorRules.MaxImpostors"/>
 /// are mirrored. An impostor:
 /// <list type="bullet">
 /// <item>is a registered S5 grid (C2 pose rows as any root) with GridAdd bit 6 (<see cref="S7tImpostorRules.GridFlagImpostor"/>);
 /// only its root grid is mirrored (no subgrids);</item>
 /// <item>carries cube parts and fat blocks only: the exporter's diffs pass <see cref="S7tImpostorRules.Filter{T}"/>, which
 /// holds back edges and subparts (never requested as models, never sent);</item>
 /// <item>is never in the S6 collision skins or the S7 room sets (<c>S7pSolid</c>); its dynamic subpart rows are not
 /// written (<c>SubgridPoses</c>); SE's light rows (60 m) and effect rows (250 m) never reach it;</item>
 /// <item>is upgraded when the S5 entity set takes it (it came within the S7a radius): a GridAdd without the bit, then
 /// its held-back parts, at most <see cref="S7tImpostorRules.MaxFlushPerTick"/> a tick;</item>
 /// <item>is demoted instead of removed when the entity set lets a root go that is still inside the reach (the parts
 /// already sent stay in Subnautica, nothing finer follows);</item>
 /// <item>leaves (EntityRemove) when it leaves the reach, the toggle goes off or the policy is not Site.</item>
 /// </list>
 /// Toggle <c>de.s7t {impostors: on|off}</c>, env <c>DEEPENGINEERS_IMPOSTORS</c> (default on). Row <c>s7t.impostor</c> at
 /// 1 Hz while any impostor exists or a counter moved. Update thread; every step guarded (an exception logs once).
 /// </summary>
    internal static partial class GridMirrorProducer
    {
        private static readonly List<MyEntity> _s7tQuery = new List<MyEntity>(128);
        private static MyCubeGrid[] _s7tCand = new MyCubeGrid[64];
        private static long[] _s7tIds = new long[64];
        private static double[] _s7tDist = new double[64];
        private static readonly int[] _s7tPick = new int[S7tImpostorRules.MaxAdmitsPerQuery];
        private static readonly List<long> _s7tDrop = new List<long>();
        private static readonly List<ulong> _s7tKeys = new List<ulong>(S7tImpostorRules.MaxFlushPerTick);
        private static readonly List<string> _s7tDropWhy = new List<string>();
        private static ulong _s7tTick;
        private static bool _s7tErrorLogged;
        private static ulong _s7tNextRowMs;
        private static long _s7tRowSig;
        // counters (s7t.impostor rows, de.s7t composite)
        private static long _s7tQueries, _s7tCandidates, _s7tAdmitted, _s7tUpgrades, _s7tDemotions, _s7tLeaves, _s7tCapDrops, _s7tSmall,
            _s7tTooBig, _s7tHeldParts, _s7tFlushed, _s7tFlushDeferred, _s7tQueryResults;
        private static double _s7tQueryMs, _s7tQueryMsMax;

        /// <summary>T3: impostors mirrored now.</summary>
        internal static int ImpostorCount
        {
            get
            {
                int n = 0;
                foreach (MirroredGrid g in Grids.Values) if (g.Impostor) n++;
                return n;
            }
        }

        /// <summary>S5PreTick hook, after the entity set: upgrades, leaves, a query every 30 ticks, the 1 Hz row.</summary>
        private static void S7tPreTick(ulong nowMs, Vector3D cam, bool allowAdd)
        {
            try
            {
                _s7tTick++;
                SiteMap site = SiteBinding.Map;
                bool active = S7tSettings.Impostors && S5Active && PictureHooks.SiteNow() && site != null;
                Vec3 camV = new Vec3(cam.X, cam.Y, cam.Z);
                Vec3 up = site != null ? (camV - site.C).Normalized() : Vec3.UnitY;

                // members: upgrade (the entity set took it), leave (out of the reach, inactive, closed)
                _s7tDrop.Clear();
                _s7tDropWhy.Clear();
                foreach (MirroredGrid g in Grids.Values)
                {
                    if (!g.Impostor) continue;
                    if (EntitySet.Tracker.Contains(g.Id)) { S7tUpgrade(g); continue; }
                    if (!active) { _s7tDrop.Add(g.Id); _s7tDropWhy.Add(S7tSettings.Impostors ? "impostor: not under the Site picture policy" : "impostor: de.s7t impostors off"); continue; }
                    MyCubeGrid grid = g.Grid;
                    if (grid == null || grid.Closed || grid.MarkedForClose) continue; // PruneClosed sends its EntityRemove
                    double h, u, r;
                    S7tReach(grid, camV, up, out h, out u, out r);
                    if (!S7tImpostorRules.InReach(h, u, r, true, S7tCamAltM()))   // R1: the column from the sea up
                    {
                        _s7tDrop.Add(g.Id);
                        _s7tDropWhy.Add("impostor out of the reach (horizontal " + h.ToString("0", CultureInfo.InvariantCulture) + " m, up " + u.ToString("0", CultureInfo.InvariantCulture) + " m)");
                    }
                }
                for (int i = 0; i < _s7tDrop.Count; i++)
                {
                    UnregisterS5(_s7tDrop[i], _s7tDropWhy[i]);
                    _s7tLeaves++;
                }

                // a query every QueryEveryTicks ticks
                if (active && allowAdd && _s7tTick % (ulong)S7tImpostorRules.QueryEveryTicks == 0) S7tQuery(camV, up);
                if (Gate.Streaming) S7tFlush();
                S7tRowIfDue(nowMs, active);
            }
            catch (Exception e)
            {
                if (_s7tErrorLogged) return;
                _s7tErrorLogged = true;
                DeLog.Info("DE.S7t impostor step error " + e.GetType().Name + ": " + e.Message + " (logged once; S5 keeps running)\n" + e.StackTrace);
            }
        }

        /// <summary>The reach split of a grid: horizontal distance and height above the camera of its AABB centre, and its half-diagonal.</summary>

 /// <summary>The camera's height above the sea (m, ≥ 0; SN y = 0 is the sea; under water 0) for the impostor column (R1).</summary>
        private static double S7tCamAltM()
        {
            float y = FrameWriter.C5CamSnY;
            return float.IsNaN(y) || float.IsInfinity(y) || y < 0f ? 0.0 : y;
        }

        private static void S7tReach(MyCubeGrid grid, Vec3 cam, Vec3 up, out double horizontalM, out double upM, out double radiusM)
        {
            BoundingBoxD box = grid.PositionComp.WorldAABB;
            Vector3D c = box.Center;
            radiusM = box.HalfExtents.Length();
            S7tImpostorRules.Split(new Vec3(c.X, c.Y, c.Z) - cam, up, out horizontalM, out upM);
        }

        private static void S7tQuery(Vec3 cam, Vec3 up)
        {
            long t0 = Stopwatch.GetTimestamp();
            _s7tQueries++;
            double qUp, qRadius;
            S7tImpostorRules.QuerySphere(S7tCamAltM(), out qUp, out qRadius);   // R1: the sphere encloses the column from the sea up
            Vec3 centre = cam + up * qUp;
            var sphere = new BoundingSphereD(new Vector3D(centre.X, centre.Y, centre.Z), qRadius);
            _s7tQuery.Clear();
            MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref sphere, _s7tQuery, MyEntityQueryType.Both);
            _s7tQueryResults += _s7tQuery.Count;
            int n = 0;
            for (int i = 0; i < _s7tQuery.Count; i++)
            {
                var grid = _s7tQuery[i] as MyCubeGrid;
                if (grid == null) continue;
                long root = EntitySet.RootOf(grid);
                if (root != grid.EntityId) continue;                                     // roots only (no subgrids)
                if (Grids.ContainsKey(root) || EntitySet.Tracker.Contains(root)) continue; // mirrored already (full or impostor)
                string why;
                if (!EntitySet.Mirrorable(grid, out why)) continue;
                string refusal = S7tImpostorRules.Refusal(grid.GridSizeEnum == MyCubeSize.Large, grid.BlocksCount, grid.IsPreview);
                double h, u, r;
                S7tReach(grid, cam, up, out h, out u, out r);
                if (!S7tImpostorRules.InReach(h, u, r, false, S7tCamAltM())) continue;
                if (refusal == "small") { _s7tSmall++; continue; }
                if (refusal == "tooBig") { _s7tTooBig++; continue; }
                if (refusal != null) continue;
                if (n >= _s7tIds.Length)
                {
                    int cap = _s7tIds.Length * 2;
                    Array.Resize(ref _s7tCand, cap);
                    Array.Resize(ref _s7tIds, cap);
                    Array.Resize(ref _s7tDist, cap);
                }
                _s7tCand[n] = grid;
                _s7tIds[n] = root;
                _s7tDist[n] = Math.Sqrt(h * h + u * u);
                n++;
            }
            _s7tCandidates += n;
            int capDrops;
            int k = S7tImpostorRules.Admit(_s7tIds, _s7tDist, n, ImpostorCount, _s7tPick, out capDrops);
            _s7tCapDrops += capDrops;
            for (int i = 0; i < k; i++)
            {
                MyCubeGrid grid = _s7tCand[_s7tPick[i]];
                RegisterS5(grid, "impostor (S7t vertical reach)", false, null);
                MirroredGrid g;
                if (!Grids.TryGetValue(grid.EntityId, out g)) continue;
                g.Impostor = true;
                if (g.ImpostorHeld == null) g.ImpostorHeld = new Dictionary<ulong, PartSnapshot>();
                _s7tAdmitted++;
                DeLog.Info("DE.S7t impostor " + g.Id + " in (" + grid.BlocksCount + " blocks, " + _s7tDist[_s7tPick[i]].ToString("0", CultureInfo.InvariantCulture) +
                           " m from the camera): cube parts and fat blocks only, GridAdd bit 6");
            }
            for (int i = 0; i < n; i++) _s7tCand[i] = null;
            _s7tQuery.Clear();
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            _s7tQueryMs = ms;
            if (ms > _s7tQueryMsMax) _s7tQueryMsMax = ms;
        }

        /// <summary>The entity set took an impostor (it came within the S7a radius): the bit goes, the held-back parts follow.</summary>
        private static void S7tUpgrade(MirroredGrid g)
        {
            g.Impostor = false;
            _s7tUpgrades++;
            DeLog.Info("DE.S7t impostor " + g.Id + " upgraded to a full mirror (within the S7a radius): GridAdd without bit 6, " +
                       (g.ImpostorHeld != null ? g.ImpostorHeld.Count : 0) + " held-back part(s) follow");
        }

        /// <summary>
 /// The entity set lets <paramref name="id"/> go (outside the leave radius for its keep time): a root grid still inside
 /// the reach stays as an impostor (true) instead of an EntityRemove.
 /// </summary>
        private static bool S7tDemote(long id, string why)
        {
            try
            {
                MirroredGrid g;
                if (!Grids.TryGetValue(id, out g) || g.Impostor || g.Floating || g.Projection || g.Missile || g.Pinned) return false;
                MyCubeGrid grid = g.Grid;
                SiteMap site = SiteBinding.Map;
                if (grid == null || grid.Closed || site == null || !S7tSettings.Impostors || !S5Active || !PictureHooks.SiteNow()) return false;
                if (EntitySet.RootOf(grid) != id) return false;
                if (S7tImpostorRules.Refusal(grid.GridSizeEnum == MyCubeSize.Large, grid.BlocksCount, grid.IsPreview) != null) return false;
                if (ImpostorCount >= S7tImpostorRules.MaxImpostors) { _s7tCapDrops++; return false; }
                MyCamera camera = MySector.MainCamera;
                if (camera == null) return false;
                Vector3D ct = camera.WorldMatrix.Translation;
                var cam = new Vec3(ct.X, ct.Y, ct.Z);
                double h, u, r;
                S7tReach(grid, cam, (cam - site.C).Normalized(), out h, out u, out r);
                if (!S7tImpostorRules.InReach(h, u, r, true, S7tCamAltM())) return false;
                g.Impostor = true;
                if (g.ImpostorHeld == null) g.ImpostorHeld = new Dictionary<ulong, PartSnapshot>();
                _s7tDemotions++;
                DeLog.Info("DE.S7t grid " + id + " demoted to an impostor (" + why + "; still inside the vertical reach): GridAdd bit 6, its parts stay");
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>S5Emit hook: the impostor filter of one diff (in place); true when nothing is left to send.</summary>
        private static bool S7tFilter(MirroredGrid g, List<PartSnapshot> adds, List<PartSnapshot> replaces, List<PartSnapshot> removes, List<PartSnapshot> recolors)
        {
            bool impostor = g != null && (g.Impostor || S10Se.PreviewImpostor(g.Grid)); // S10 hook (round 2 A): a paste preview over the cap
            if (g == null || (!impostor && (g.ImpostorHeld == null || g.ImpostorHeld.Count == 0))) return false;
            if (g.ImpostorHeld == null) g.ImpostorHeld = new Dictionary<ulong, PartSnapshot>();
            int newly = S7tImpostorRules.Filter(impostor, g.ImpostorHeld, adds, replaces, removes, recolors,
                                                p => p.PartKey, p => p.Flags, S7tRecolour);
            _s7tHeldParts += newly;
            return adds.Count + replaces.Count + removes.Count + recolors.Count == 0;
        }

        private static readonly Func<PartSnapshot, PartSnapshot, PartSnapshot> S7tRecolour = (held, now) =>
        {
            held.H = now.H;
            held.S = now.S;
            held.V = now.V;
            return held;
        };

        /// <summary>After an upgrade: the held-back parts as PartAdds (lane first-send), at most MaxFlushPerTick a tick.</summary>
        private static void S7tFlush()
        {
            int budget = S7tImpostorRules.MaxFlushPerTick;
            ulong now = DeepEngineers.Wire.Native.Win32.TickCount64();
            foreach (MirroredGrid g in Grids.Values)
            {
                if (g.Impostor || g.ImpostorHeld == null || g.ImpostorHeld.Count == 0) continue;
                // its GridAdd without the bit goes first: enqueued (SentGridFlags) and written to the ring
                if (g.GridAddIndex < 0 || (g.SentGridFlags & S7tImpostorRules.GridFlagImpostor) != 0 || !C4.IsDone(g.GridAddIndex)) continue;
                if (budget <= 0) { _s7tFlushDeferred++; continue; }
                List<ulong> keys = _s7tKeys;
                keys.Clear();
                foreach (KeyValuePair<ulong, PartSnapshot> kv in g.ImpostorHeld)
                {
                    if (keys.Count >= budget) break;
                    keys.Add(kv.Key);
                }
                foreach (ulong k in keys)
                {
                    PartSnapshot p = g.ImpostorHeld[k];
                    g.ImpostorHeld.Remove(k);
                    EnsureModel(now, p);
                    C4.Enqueue(C4Lanes.FirstSend, S3Codec.PartAddMsg(g.Id, p));
                    _partAdds++;
                    _s7tFlushed++;
                }
                budget -= keys.Count;
                g.PartsVersion++;
                if (g.ImpostorHeld.Count > 0) _s7tFlushDeferred++;
            }
        }

        /// <summary>S5Forget hook: the counters stay (session totals), the query state goes.</summary>
        private static void S7tForget(string why)
        {
            _s7tQuery.Clear();
            for (int i = 0; i < _s7tCand.Length; i++) _s7tCand[i] = null;
        }

        /// <summary>de.s7t composite: the impostor part.</summary>
        internal static Dictionary<string, object> S7tImpostorStatus()
        {
            int held = 0;
            var ids = new List<object>();
            foreach (MirroredGrid g in Grids.Values)
            {
                if (g.ImpostorHeld != null) held += g.ImpostorHeld.Count;
                if (g.Impostor && ids.Count < 16) ids.Add(g.Id.ToString(CultureInfo.InvariantCulture));
            }
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "on", S7tSettings.Impostors }, { "members", ImpostorCount }, { "ids", ids }, { "heldNow", held },
                { "queries", _s7tQueries }, { "queryResults", _s7tQueryResults }, { "candidates", _s7tCandidates }, { "admitted", _s7tAdmitted },
                { "upgrades", _s7tUpgrades }, { "demotions", _s7tDemotions }, { "leaves", _s7tLeaves }, { "capDrops", _s7tCapDrops },
                { "small", _s7tSmall }, { "tooBig", _s7tTooBig }, { "heldParts", _s7tHeldParts }, { "flushed", _s7tFlushed },
                { "flushDeferred", _s7tFlushDeferred }, { "queryMs", Math.Round(_s7tQueryMs, 3) }, { "queryMsMax", Math.Round(_s7tQueryMsMax, 3) },
                { "reachUpM", S7tImpostorRules.UpEnterM }, { "horizontalM", S7tImpostorRules.HorizontalEnterM }, { "max", S7tImpostorRules.MaxImpostors },
            };
        }

        /// <summary>The <c>s7t.impostor</c> row (CONTRACT §5.4 "S7t impostors"): 1 Hz while impostors exist or a counter moved.</summary>
        private static void S7tRowIfDue(ulong nowMs, bool active)
        {
            if (nowMs < _s7tNextRowMs) return;
            _s7tNextRowMs = nowMs + 1000;
            int members = ImpostorCount;
            long sig = _s7tAdmitted * 31 + _s7tUpgrades * 17 + _s7tDemotions * 13 + _s7tLeaves * 7 + _s7tCapDrops * 5 + _s7tFlushed * 3 + _s7tHeldParts;
            if (members == 0 && sig == _s7tRowSig) return;
            _s7tRowSig = sig;
            int held = 0;
            foreach (MirroredGrid g in Grids.Values) if (g.ImpostorHeld != null) held += g.ImpostorHeld.Count;
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(320);
            sb.Append("{\"t\":"); JsonText.AppendString(sb, DateTime.UtcNow.ToString("o", ci));
            sb.Append(",\"ev\":\"s7t.impostor\",\"tick\":").Append(_tick.ToString(ci));
            sb.Append(",\"active\":").Append(active ? "true" : "false");
            sb.Append(",\"members\":").Append(members.ToString(ci));
            sb.Append(",\"queries\":").Append(_s7tQueries.ToString(ci));
            sb.Append(",\"candidates\":").Append(_s7tCandidates.ToString(ci));
            sb.Append(",\"admitted\":").Append(_s7tAdmitted.ToString(ci));
            sb.Append(",\"upgrades\":").Append(_s7tUpgrades.ToString(ci));
            sb.Append(",\"demotions\":").Append(_s7tDemotions.ToString(ci));
            sb.Append(",\"leaves\":").Append(_s7tLeaves.ToString(ci));
            sb.Append(",\"capDrops\":").Append(_s7tCapDrops.ToString(ci));
            sb.Append(",\"small\":").Append(_s7tSmall.ToString(ci));
            sb.Append(",\"tooBig\":").Append(_s7tTooBig.ToString(ci));
            sb.Append(",\"heldParts\":").Append(_s7tHeldParts.ToString(ci));
            sb.Append(",\"heldNow\":").Append(held.ToString(ci));
            sb.Append(",\"flushed\":").Append(_s7tFlushed.ToString(ci));
            sb.Append(",\"flushDeferred\":").Append(_s7tFlushDeferred.ToString(ci));
            sb.Append(",\"queryMs\":").Append(_s7tQueryMs.ToString("0.###", ci));
            sb.Append(",\"queryMsMax\":").Append(_s7tQueryMsMax.ToString("0.###", ci)).Append('}');
            Jsonl.WriteLineBuffered(sb.ToString());
        }
    }
}
