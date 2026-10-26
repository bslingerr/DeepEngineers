using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.Utils;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The S3 lab command <c>de.s3</c> (C11 and <c>/de s3</c>; dev builds only; syntax <see cref="S3Args"/>) and the
 /// <c>de.motion {kind: "yaw"}</c> sine of S3.N2:
 /// <list type="bullet">
 /// <item>place: a STATIC large grid holding <c>LargeBlockArmorBlock</c> (a single block, a 3×3 floor or a rows × cols
 /// wall) whose block (0, 0, 0) is centred on SN (x, h, z), h = the seabed height (lower half buried) or the point the
 /// harness gives (<c>place.sn</c>) (<see cref="S3Placement"/>); any previous S3 grid is closed first; the grid is
 /// registered with <see cref="GridMirrorProducer"/>; a GPS marker at the grid origin (S3.N2); the SE world matrix and
 /// P = F⁻¹(x, h, z) are logged (S3.C2); with <c>export</c> SE's coloured texture of the parts' material is baked once
 /// the models are loaded (S3.C13), at the PNG path the reply names;</item>
 /// <item>status (state), remove, export, trace, gps on/off (s3.gps rows every frame, S3.N2), pixel (SE's pixel of a
 /// corner of the placed grid or of an SN point, S3.N3).</item>
 /// <item>S7 review F1/F8/F9: <c>plate: {orientation: "room"}</c> (or <c>plate: "room"</c>) places the S7
 /// room fixture of <see cref="S3Placement.RoomLayout"/> as one static grid (per block its own definition and
 /// <c>BlockOrientation</c>, every one 1 × 1 × 1); the battery is charged to full when it came up below half
 /// the reply carries
 /// <c>plate.orientation</c> "room" and <c>room</c> {door, vent, battery, light, spots, window: entity ids as decimal
 /// strings; interior SN box}.</item>
 /// </list>
 /// Spawn path:
 /// <c>MyObjectBuilderSerializerKeen.CreateNewObject&lt;MyObjectBuilder_CubeGrid&gt;</c>; <c>PositionAndOrientation =
 /// new MyPositionAndOrientation(Vector3D, Vector3 forward, Vector3 up)</c>; <c>GridSizeEnum</c>, <c>IsStatic = true</c>,
 /// <c>CreatePhysics = true</c>, <c>PersistentFlags |= Enabled | InScene</c>, <c>EntityId = MyEntityIdentifier.AllocateId</c>;
 /// per block <c>MyObjectBuilderSerializerKeen.CreateNewObject(SerializableDefinitionId)</c> as
 /// <c>MyObjectBuilder_CubeBlock</c> with <c>Min</c>, <c>ColorMaskHSV</c>, <c>IntegrityPercent = BuildPercent = 1</c>
 /// (the full block, not a construction stage), <c>EntityId</c>; then <c>MyEntities.CreateFromObjectBuilderAndAdd</c> (synchronous, update thread; <c>MyFakes.ENABLE_LARGE_OFFSET</c> is false), then <c>MyEntity.Save =
 /// false</c>. A static grid gets no
 /// dynamic physics, so it neither sinks nor drifts in the Water Mod's water, and it only turns dynamic on a split when
 /// StationVoxelSupport is off). The grid origin is the centre of cell (0, 0, 0) (1×1×1 blocks only:
 /// <c>MyCubeBlock.GetBlockGridOffset</c> is 0 for them). GPS: <c>new MyGps { Name, DisplayName, Description, Coords,
 /// ShowOnHud, AlwaysVisible, DiscardAt = null }</c>, <c>UpdateHash</c>, <c>MySession.Static.Gpss.SendAddGpsRequest(
 /// MySession.Static.LocalPlayerId, ref gps, 0, false)</c>; removed with <c>SendDeleteGpsRequest(identityId, hash)</c>.
 /// <para>h = "raycast": the command relays <c>de.s3 {action: "probe", x, z}</c> to Subnautica over the chat relay
 /// (CtlToSn, only while SE may hold devControl bit 1, i.e. no harness attached), asks again every 2 s while Subnautica
 /// answers "pending" (its terrain collider there is not built yet), and places the block on the seabedY; when
 /// Subnautica cannot be asked (a harness attached: the harness probes Subnautica itself and passes h) or does not
 /// answer within 30 s, the lab config value is used and the reason is logged and returned in hSource.</para>
 /// Update thread only; never throws (failures answer ok: false and log <c>DE.S3 …</c>).
 /// </summary>
    internal static class S3Commands
    {
        public const string GpsName = "DE.S3 block";
        /// <summary>Pose rows every tick for this long after a placement (longer windows: <c>de.s3 {trace: s}</c>).</summary>
        public const double AutoTraceSeconds = 10;

        /// <summary>
 /// Set by the plugin: relays a command to Subnautica and calls back with the reply object (null on timeout) on the
 /// update thread; returns null when relayed, else why not.
 /// </summary>
        public static Func<string, Dictionary<string, object>, Action<IDictionary<string, object>>, string> RelayToSn;

        /// <summary>A pending h = "raycast" placement: asked again every <see cref="ProbeRetryMs"/> while Subnautica answers "pending".</summary>
        private sealed class PendingProbe
        {
            public S3PlaceRequest Req;
            public string Via;
            public ulong DeadlineMs;
            public ulong RetryAtMs;
            public bool Waiting;
            public int Asks;
        }

        public const ulong ProbeRetryMs = 2000, ProbeDeadlineMs = 30000;
        private static PendingProbe _probe;

        private static int _gpsHash;
        private static bool _gpsAdded;
        private static long _gpsIdentity;

        // de.motion kind yaw
        private static YawPlan _yaw;
        private static int _yawK;
        private static Vec3 _yawPosSe, _yawFwdSn, _yawUpSn;
        private static ulong _yawStartTick;

        // ================================================================================================= dispatch

        public static Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            if (cmd == "de.motion") return Motion(args, via);
            if (cmd != "de.s3") return null;
            try
            {
                switch (S3Args.ActionOf(args))
                {
                    case "status": return Status();
                    case "mirror": return Mirror(args, via);
                    case "remove": return Remove("de.s3 remove via " + via);
                    case "export": return Export();
                    case "gps": return Gps(args);
                    case "pixel": return Pixel(args, via);
                    case "trace":
                    {
                        double s = MiniJson.GetNumber(args, "trace") ?? 10.0;
                        GridMirrorProducer.Trace(s);
                        string line = "DE.S3 trace: s3.pose and s3.gps rows every tick for " + s.ToString("0.###", CultureInfo.InvariantCulture) + " s (se-*.jsonl; S3.C2, S3.N2)";
                        DeLog.Info(line);
                        return Ok(line);
                    }
                    default: return Place(args, via);
                }
            }
            catch (Exception e)
            {
                DeLog.Info("DE.Error de.s3 " + e.GetType().Name + ": " + e.Message + " " + e.StackTrace);
                return Fail("de.s3 failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>Update thread, every tick in world (GridMirrorProducer, before PrepareForDraw): the yaw sine and a pending probe.</summary>
        public static void Tick(ulong nowMs)
        {
            TickYaw();
            PendingProbe pp = _probe;
            if (pp == null || pp.Waiting || pp.RetryAtMs == 0 || nowMs < pp.RetryAtMs) return;
            pp.RetryAtMs = 0;
            var relay = RelayToSn;
            string notRelayed = relay != null ? AskProbe(pp, relay) : "no relay";
            if (notRelayed == null) return;
            _probe = null;
            pp.Req.FallBackToLabConfig("cannot ask Subnautica again: " + notRelayed);
            SiteMap site;
            string why;
            if (!S1Commands.Ready(out site, out why)) { DeLog.Info("DE.S3 placement NOT done: " + why); return; }
            DeLog.Info("DE.S3 seabed probe: " + pp.Req.RaycastFailure + "; placing at the lab config h=" + N(pp.Req.H));
            DoPlace(pp.Req, site, pp.Via);
        }

        // ================================================================================================= place

        private static Dictionary<string, object> Place(IDictionary<string, object> args, string via)
        {
            S3PlaceRequest req;
            string why;
            if (!S3Args.TryParsePlace(args, out req, out why)) return Fail("de.s3: " + why + " (" + S3Args.ChatUsage + ")");
            SiteMap site;
            if (!S1Commands.Ready(out site, out why)) return Fail("de.s3: " + why);
            if (!ActivationGate.IsOpen) return Fail("de.s3: activation gate " + ActivationGate.State + " (offline single player only)");
            _s11Dynamic = S11Dynamic(args); // S11 hook: {place: {..., dynamic: true}} for the grab oracle (S3 places static grids)
            if (!req.Raycast) return DoPlace(req, site, via);

            // h = raycast: ask Subnautica (chat relay only), place on the reply; else the lab config value
            var relay = RelayToSn;
            if (relay == null)
            {
                req.FallBackToLabConfig("no relay to Subnautica in this build");
                return DoPlace(req, site, via);
            }
            var pp = new PendingProbe { Req = req, Via = via, DeadlineMs = Win32.TickCount64() + ProbeDeadlineMs };
            string notRelayed = AskProbe(pp, relay);
            if (notRelayed != null)
            {
                // with a harness attached SE cannot write CtlToSn: the harness probes Subnautica itself and passes h
                req.FallBackToLabConfig("cannot ask Subnautica: " + notRelayed);
                DeLog.Info("DE.S3 h=raycast: " + req.RaycastFailure + "; placing at the lab config h=" + N(req.H));
                return DoPlace(req, site, via);
            }
            _probe = pp;
            string line = "DE.S3 placement pending: asked Subnautica for the seabed at (" + N(req.X) + ", " + N(req.Z) +
                          ") (de.s3 {action: probe}); the block is placed when it answers (lab config after " + ProbeDeadlineMs / 1000 + " s)";
            DeLog.Info(line);
            var r = Ok(line);
            r["pending"] = true;
            return r;
        }

        /// <summary>Relays one probe; the reply places the block (height), schedules another ask (pending) or falls back (failed).</summary>
        private static string AskProbe(PendingProbe pp, Func<string, Dictionary<string, object>, Action<IDictionary<string, object>>, string> relay)
        {
            pp.Asks++;
            pp.Waiting = true;
            S3PlaceRequest req = pp.Req;
            return relay("de.s3", S3Args.ProbeArgs(req.X, req.Z), reply =>
            {
                try
                {
                    pp.Waiting = false;
                    if (!ReferenceEquals(_probe, pp)) return; // superseded by a newer de.s3 place or a remove
                    double h;
                    string w;
                    S3Args.Probe res = S3Args.ReadProbe(reply, out h, out w);
                    ulong now = Win32.TickCount64();
                    if (res == S3Args.Probe.Pending && now + ProbeRetryMs < pp.DeadlineMs)
                    {
                        pp.RetryAtMs = now + ProbeRetryMs;
                        DeLog.Info("DE.S3 seabed probe at (" + N(req.X) + ", " + N(req.Z) + "): " + w + "; asking again in " + ProbeRetryMs / 1000 + " s (ask " + pp.Asks + ")");
                        return;
                    }
                    _probe = null;
                    if (res == S3Args.Probe.Height)
                    {
                        req.H = h;
                        DeLog.Info("DE.S3 seabed probe (Subnautica layer-30 raycast) at (" + N(req.X) + ", " + N(req.Z) + "): seabedY=" + N(h) + " after " + pp.Asks + " ask(s)");
                    }
                    else
                    {
                        req.FallBackToLabConfig("the Subnautica probe " + (res == S3Args.Probe.Pending ? "stayed pending for " + ProbeDeadlineMs / 1000 + " s" : "failed: " + w));
                        DeLog.Info("DE.S3 seabed probe at (" + N(req.X) + ", " + N(req.Z) + "): " + req.RaycastFailure + "; placing at the lab config h=" + N(req.H) +
                                   " (pass h explicitly, e.g. /de s3 h=-153)");
                    }
                    SiteMap s2;
                    string w2;
                    if (!S1Commands.Ready(out s2, out w2)) { DeLog.Info("DE.S3 placement NOT done after the probe: " + w2); return; }
                    DoPlace(req, s2, pp.Via + " + Subnautica probe");
                }
                catch (Exception e) { DeLog.Info("DE.Error de.s3 probe reply " + e.GetType().Name + ": " + e.Message); }
            });
        }

        /// <summary>A block definition by id text ("Type/Subtype" or a CubeBlock subtype), 1 × 1 × 1 only; null with <paramref name="why"/>.</summary>
        private static MyCubeBlockDefinition Definition(string text, out string why)
        {
            why = null;
            MyDefinitionId id;
            if (text.IndexOf('/') >= 0)
            {
                if (!MyDefinitionId.TryParse(text, out id)) { why = "de.s3: bad block id '" + text + "'"; return null; }
            }
            else id = new MyDefinitionId(typeof(MyObjectBuilder_CubeBlock), text);
            MyCubeBlockDefinition def;
            if (!MyDefinitionManager.Static.TryGetCubeBlockDefinition(id, out def) || def == null) { why = "de.s3: no cube block definition " + id; return null; }
            if (def.Size != Vector3I.One) { why = "de.s3: " + id + " is " + def.Size + " cells; S3 places 1x1x1 blocks only"; return null; }
            return def;
        }

        private static bool _s11Dynamic;

        /// <summary>S11 hook: <c>{place: {..., dynamic: true}}</c> (or <c>dynamic: true</c> beside it).</summary>
        private static bool S11Dynamic(IDictionary<string, object> args)
        {
            object v;
            var p = args != null && args.TryGetValue("place", out v) ? v as IDictionary<string, object> : null;
            object d = null;
            if ((p == null || !p.TryGetValue("dynamic", out d)) && args != null) args.TryGetValue("dynamic", out d);
            return d is bool ? (bool)d : string.Equals(Convert.ToString(d, CultureInfo.InvariantCulture), "true", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> DoPlace(S3PlaceRequest req, SiteMap site, string via)
        {
            _probe = null; // a placement supersedes any probe still pending
 // the block definition (1x1x1 only: the grid origin is then the block centre)
            string why0;
            MyCubeBlockDefinition def = Definition(req.Plate == "room" ? "MyObjectBuilder_CubeBlock/LargeBlockArmorBlock" : req.Block, out why0);
            if (def == null) return Fail(why0);
            // review F1: the room fixture has one definition and orientation per block (all large, all 1x1x1)
            List<RoomBlock> room = req.Plate == "room" ? S3Placement.RoomLayout() : null;
            var roomDefs = new Dictionary<string, MyCubeBlockDefinition>(StringComparer.Ordinal);
            if (room != null)
            {
                foreach (RoomBlock rb in room)
                {
                    if (roomDefs.ContainsKey(rb.DefinitionId)) continue;
                    MyCubeBlockDefinition d = Definition(rb.DefinitionId, out why0);
                    if (d == null) return Fail(why0 + " (the de.s3 room's " + rb.Role + ")");
                    if (d.CubeSize != def.CubeSize) return Fail("de.s3 room: " + d.Id + " is " + d.CubeSize + ", the room is " + def.CubeSize);
                    roomDefs[rb.DefinitionId] = d;
                }
            }

            int closed = CloseOurGrids("replaced by a new de.s3 place");

            SeMatrix gm = S3Placement.GridMatrix(site, req.X, req.H, req.Z, req.YawDeg);
            Vec3 back = site.SeToSn(gm.Translation);
            var gridOb = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeGrid>();
            gridOb.EntityId = MyEntityIdentifier.AllocateId();
            gridOb.GridSizeEnum = def.CubeSize;
            gridOb.IsStatic = !_s11Dynamic; // S11 hook: static unless {place: {dynamic: true}}
            gridOb.CreatePhysics = true;
            gridOb.PersistentFlags |= MyPersistentEntityFlags2.Enabled | MyPersistentEntityFlags2.InScene;
            gridOb.DisplayName = room != null ? "DE.S3 room" : "DE.S3 " + def.Id.SubtypeName;
            gridOb.PositionAndOrientation = new MyPositionAndOrientation(
                new Vector3D(gm.Translation.X, gm.Translation.Y, gm.Translation.Z),
                new Vector3((float)gm.Forward.X, (float)gm.Forward.Y, (float)gm.Forward.Z),
                new Vector3((float)gm.Up.X, (float)gm.Up.Y, (float)gm.Up.Z));
            List<int[]> cells = req.Cells();
            for (int ci = 0; ci < cells.Count; ci++)
            {
                int[] c = cells[ci];
                MyCubeBlockDefinition bd = room != null ? roomDefs[room[ci].DefinitionId] : def;
                var b = MyObjectBuilderSerializerKeen.CreateNewObject(bd.Id) as MyObjectBuilder_CubeBlock;
                if (b == null) return Fail("de.s3: " + bd.Id + " has no MyObjectBuilder_CubeBlock builder");
                b.Min = new SerializableVector3I(c[0], c[1], c[2]);
                b.EntityId = MyEntityIdentifier.AllocateId();
                b.IntegrityPercent = 1f;
                b.BuildPercent = 1f;
                if (room != null)
                {
                    Base6Directions.Direction fwd, up;
                    if (!Enum.TryParse(room[ci].Forward, out fwd) || !Enum.TryParse(room[ci].Up, out up))
                        return Fail("de.s3 room: bad orientation " + room[ci].Forward + "/" + room[ci].Up);
                    b.BlockOrientation = new SerializableBlockOrientation(fwd, up);
                }
                if (req.ColorHsv != null) b.ColorMaskHSV = new SerializableVector3((float)req.ColorHsv[0], (float)req.ColorHsv[1], (float)req.ColorHsv[2]);
                gridOb.CubeBlocks.Add(b);
            }
            var grid = MyEntities.CreateFromObjectBuilderAndAdd(gridOb, false) as MyCubeGrid;
            if (grid == null || grid.Closed) return Fail("de.s3: MyEntities.CreateFromObjectBuilderAndAdd returned no grid (see the SE log for 'ERROR Entity init')");
            // a lab test entity never goes into a world save: SE autosaves DE Lab during sessions,
 // and MyEntities.Save skips entities whose MyEntity.Save is false
            grid.Save = false;

            SeMatrix wm = FrameWriter.ToSe(grid.WorldMatrix);
            double gs = grid.GridSize;
            Vec3 lmin, lmax;
            S3Placement.CellBounds(cells, gs, out lmin, out lmax);
            Vec3 centreLocal = (lmin + lmax) * 0.5;
            Vec3 centreSe = wm.Transform(centreLocal);
            Vec3 centreSn = site.SeToSn(centreSe);
            WireEntityPose pose = PoseMath.FromSe(site, wm, wm.Translation, 1.0); // P* at the grid origin (exact there)
            WireEntityPose pw = pose.RoundToFloat32();
            var cornersSn = new List<object>(8);
            for (int i = 0; i < 8; i++) cornersSn.Add(Arr(site.SeToSn(wm.Transform(S3Placement.Corner(lmin, lmax, i)))));
            string exportPng = null;
            if (req.Export)
                exportPng = Path.Combine(MaterialExport.Directory, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-grid" +
                                         grid.EntityId.ToString(CultureInfo.InvariantCulture) + "-0_ca.png");
            var info = new S3GridInfo
            {
                LabSn = new Vec3(req.X, req.H, req.Z), HeadingDeg = req.YawDeg, HSource = req.HSource, LocalMin = lmin, LocalMax = lmax,
                ExportPng = exportPng, Label = req.Label, Spawned = true,
            };
            GridMirrorProducer.Register(grid, "de.s3 via " + via, info);
            GridMirrorProducer.Trace(AutoTraceSeconds); // S3.C2: a pose row every tick right after the placement
            string gpsLine = req.Marker ? AddGps(wm.Translation, req) : "no GPS marker";

            string line = "DE.S3 placed grid " + grid.EntityId + " (" + def.Id.SubtypeName + " x" + cells.Count + ", " + req.Plate +
                          (req.Plate != "single" ? " " + req.Rows + "x" + req.Cols : "") + ", static, " + def.CubeSize + ") block (0,0,0) centre SN (" +
                          N(req.X) + ", " + N(req.H) + ", " + N(req.Z) + ") h from " + req.HSource + " yawDeg=" + N(req.YawDeg) +
                          " -> SE P=" + V(gm.Translation) + " F(P)=" + V(back) + " |F(P)-sn|=" + G((back - new Vec3(req.X, req.H, req.Z)).Length) +
                          " m; SE world matrix R=" + V(wm.Right) + " U=" + V(wm.Up) + " B=" + V(wm.Backward) + " T=" + V(wm.Translation) +
                          " (|T - P|=" + G((wm.Translation - gm.Translation).Length) + " m; det=" + G(wm.Determinant) + ") local box " + V(lmin) + ".." + V(lmax) +
                          " centreSn=" + V(centreSn) + "; " + gpsLine + (exportPng != null ? "; colour export to " + exportPng + " once the model is loaded" : "") +
                          (closed > 0 ? "; closed " + closed + " previous S3 grid(s)" : "") + " (via " + via + ")";
            DeLog.Info(line);
            var r = Ok(line);
            r["entityId"] = grid.EntityId;
            r["gridId"] = grid.EntityId;
            r["seP"] = Arr(gm.Translation);
            r["seMatrix"] = new object[] { wm.Right.X, wm.Right.Y, wm.Right.Z, wm.Up.X, wm.Up.Y, wm.Up.Z, wm.Backward.X, wm.Backward.Y, wm.Backward.Z,
                                           wm.Translation.X, wm.Translation.Y, wm.Translation.Z };
            r["labSn"] = Arr(new Vec3(req.X, req.H, req.Z));
            r["centreSn"] = Arr(centreSn);
            r["headingDeg"] = req.YawDeg;
            r["h"] = req.H;
            r["hSource"] = req.HSource;
            r["o"] = Arr(pw.O);
            r["q"] = new object[] { pw.Q.X, pw.Q.Y, pw.Q.Z, pw.Q.W };
            r["s"] = pw.S;
            r["k"] = pw.K;
            r["pivot"] = Arr(wm.Translation);
            r["bounds"] = new object[] { lmin.X, lmin.Y, lmin.Z, lmax.X, lmax.Y, lmax.Z };
            r["localMin"] = Arr(lmin);
            r["localMax"] = Arr(lmax);
            r["cornersSn"] = cornersSn;
            r["blocks"] = cells.Count;
            r["parts"] = cells.Count; // the image manifest reads "parts" as the block count (half = bounds/2 for one block)
            var cellList = new List<object>(cells.Count);
            foreach (int[] c in cells) cellList.Add(new object[] { (double)c[0], (double)c[1], (double)c[2] });
            r["cells"] = cellList;
            if (req.Plate != "single")
                r["plate"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "cells", cellList }, { "half", gs / 2 }, { "rows", req.Rows }, { "cols", req.Cols }, { "orientation", req.Plate }, { "exportPng", exportPng },
                };
            if (exportPng != null) r["exportPng"] = exportPng;
            if (req.Label != null) r["label"] = req.Label;
            r["streaming"] = GridMirrorProducer.Streaming;
            if (room != null) r["room"] = RoomReply(grid, room, site, wm, gs);
            return r;
        }

        /// <summary>
 /// Review F1/F8/F9: the room fixture's block ids by role (decimal strings), the battery charged when it came up below
 /// half, the interior's SN box (cell centres ± half a block). Never throws (a missing block is named in the reply).
 /// </summary>
        private static Dictionary<string, object> RoomReply(MyCubeGrid grid, List<RoomBlock> room, SiteMap site, SeMatrix wm, double gs)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            var spots = new List<object>();
            var missing = new List<object>();
            foreach (RoomBlock rb in room)
            {
                if (rb.Role == "shell") continue;
                MySlimBlock slim = grid.GetCubeBlock(new Vector3I(rb.Cell[0], rb.Cell[1], rb.Cell[2]));
                MyCubeBlock fat = slim != null ? slim.FatBlock : null;
                if (fat == null) { missing.Add(rb.Role + "@" + rb.Sn[0] + "," + rb.Sn[1] + "," + rb.Sn[2]); continue; }
                string id = fat.EntityId.ToString(CultureInfo.InvariantCulture);
                if (rb.Role == "spot") spots.Add(id);
                else d[rb.Role] = id;
                var bat = fat as MyBatteryBlock;
                if (bat != null)
                {
                    try
                    {
                        if (bat.CurrentStoredPower < 0.5f * bat.MaxStoredPower) bat.CurrentStoredPower = bat.MaxStoredPower;
                        d["batteryMWh"] = Math.Round(bat.CurrentStoredPower, 3);
                    }
                    catch (Exception e) { d["batteryError"] = e.GetType().Name + ": " + e.Message; }
                }
            }
            d["spots"] = spots;
            if (missing.Count > 0) d["missing"] = missing;
            // the interior: SN offsets 1..3 blocks in each axis; its SN box from the grid-local cells (1, 1, -1).. (3, 3, -3)
            Vec3 a = site.SeToSn(wm.Transform(new Vec3(1 * gs - gs / 2, 1 * gs - gs / 2, -1 * gs + gs / 2)));
            Vec3 b = site.SeToSn(wm.Transform(new Vec3(3 * gs + gs / 2, 3 * gs + gs / 2, -3 * gs - gs / 2)));
            d["interiorMinSn"] = Arr(new Vec3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)));
            d["interiorMaxSn"] = Arr(new Vec3(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));
            d["interiorCentreSn"] = Arr(site.SeToSn(wm.Transform(new Vec3(2 * gs, 2 * gs, -2 * gs))));
            DeLog.Info("DE.S3 room fixture on grid " + grid.EntityId + ": door " + (d.ContainsKey("door") ? d["door"] : "MISSING") + ", vent " +
                       (d.ContainsKey("vent") ? d["vent"] : "MISSING") + ", light " + (d.ContainsKey("light") ? d["light"] : "MISSING") + ", " + spots.Count +
                       " spotlight(s), battery " + (d.ContainsKey("batteryMWh") ? d["batteryMWh"] + " MWh" : "MISSING") +
                       (missing.Count > 0 ? "; missing " + string.Join(", ", missing.ConvertAll(x => (string)x).ToArray()) : ""));
            return d;
        }

        private static string AddGps(Vec3 centre, S3PlaceRequest req)
        {
            try
            {
                MySession s = MySession.Static;
                if (s == null || s.Gpss == null) return "GPS: no session";
                RemoveGps();
                long identity = s.LocalPlayerId;
                if (identity == 0) return "GPS: no local player identity";
                var gps = new MyGps
                {
                    Name = GpsName,
                    DisplayName = GpsName,
                    Description = "S3 lab block centre: SN (" + N(req.X) + ", " + N(req.H) + ", " + N(req.Z) + ")",
                    Coords = new Vector3D(centre.X, centre.Y, centre.Z),
                    ShowOnHud = true,
                    AlwaysVisible = true,
                };
                gps.DiscardAt = null;
                gps.UpdateHash();
                s.Gpss.SendAddGpsRequest(identity, ref gps, 0L, false);
                _gpsHash = gps.Hash;
                _gpsIdentity = identity;
                _gpsAdded = true;
                return "GPS '" + GpsName + "' at the grid origin (hash " + gps.Hash + ", S3.N2 marker)";
            }
            catch (Exception e)
            {
                return "GPS FAILED " + e.GetType().Name + ": " + e.Message;
            }
        }

        private static void RemoveGps()
        {
            if (!_gpsAdded) return;
            try
            {
                MySession s = MySession.Static;
                if (s != null && s.Gpss != null) s.Gpss.SendDeleteGpsRequest(_gpsIdentity, _gpsHash);
            }
            catch (Exception e) { DeLog.Info("DE.S3 GPS remove failed " + e.GetType().Name + ": " + e.Message); }
            _gpsAdded = false;
        }

        // ================================================================================================= others

        private static int CloseOurGrids(string why)
        {
            int n = 0;
            foreach (MyCubeGrid g in GridMirrorProducer.Registered(true))     // lab-spawned grids only: never a player's grid (de.s3 mirror)
            {
                if (g == null || g.Closed || g.MarkedForClose) continue;
                DeLog.Info("DE.S3 closing grid " + g.EntityId + " (" + why + ")");
                g.Close();
                n++;
            }
            return n;
        }

        // ================================================================================================= mirror (hand-built grids)
 /// <summary>

 /// <c>mirror</c> / <c>mirror near</c> = the grid closest to the local character within <see cref="MirrorNearM"/> m;
 /// <c>mirror all</c> = every grid within that radius; <c>mirror &lt;entityId&gt;</c> (decimal or 0x hex) = that grid.
 /// A registered grid streams its blocks and follows block add/remove (PartAdd/PartRemove), so the player keeps
 /// building after one command. These grids are never closed by remove or the next place (<see cref="S3GridInfo.Spawned"/>).
 /// </summary>
        private const double MirrorNearM = 150.0;

        private static Dictionary<string, object> Mirror(IDictionary<string, object> args, string via)
        {
            SiteMap site;
            string why;
            if (!S1Commands.Ready(out site, out why)) return Fail("de.s3 mirror: " + why);
            if (!ActivationGate.IsOpen) return Fail("de.s3 mirror: activation gate " + ActivationGate.State + " (offline single player only)");
            object sel = args != null && args.ContainsKey("mirror") ? args["mirror"] : null;
            string s = sel == null ? "near" : Convert.ToString(sel, CultureInfo.InvariantCulture).Trim();
            if (s.Length == 0 || s.Equals("true", StringComparison.OrdinalIgnoreCase)) s = "near";
            var picked = new List<MyCubeGrid>();
            long id;
            bool hex = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if ((hex && long.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id)) ||
                (!hex && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out id)))
            {
                MyEntity e;
                if (!MyEntities.TryGetEntityById(id, out e) || !(e is MyCubeGrid)) return Fail("de.s3 mirror: no grid with entity id " + s);
                picked.Add((MyCubeGrid)e);
            }
            else
            {
                bool all = s.Equals("all", StringComparison.OrdinalIgnoreCase);
                if (!all && !s.Equals("near", StringComparison.OrdinalIgnoreCase)) return Fail("de.s3 mirror: " + S3Args.ChatUsage);
                var ch = MySession.Static != null ? MySession.Static.LocalCharacter : null;
                if (ch == null) return Fail("de.s3 mirror: no local character");
                Vector3D p = ch.PositionComp.GetPosition();
                var sphere = new BoundingSphereD(p, MirrorNearM);
                var found = new List<MyEntity>();
                MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref sphere, found);
                MyCubeGrid best = null;
                double bestD = double.MaxValue;
                foreach (MyEntity e in found)
                {
                    var g = e as MyCubeGrid;
                    if (g == null || g.Closed || g.MarkedForClose) continue;
                    double d = Vector3D.Distance(g.PositionComp.WorldAABB.Center, p);
                    if (all) picked.Add(g);
                    else if (d < bestD) { bestD = d; best = g; }
                }
                if (!all && best != null) picked.Add(best);
                if (picked.Count == 0) return Fail("de.s3 mirror: no grid within " + MirrorNearM.ToString("0", CultureInfo.InvariantCulture) + " m of the character");
            }
            var lines = new List<string>();
            foreach (MyCubeGrid grid in picked)
            {
                double gs = grid.GridSize;
                Vector3D t = grid.WorldMatrix.Translation;
                var info = new S3GridInfo
                {
                    LabSn = site.SeToSn(new Vec3(t.X, t.Y, t.Z)), HeadingDeg = 0, HSource = "mirror",
                    LocalMin = new Vec3((grid.Min.X - 0.5) * gs, (grid.Min.Y - 0.5) * gs, (grid.Min.Z - 0.5) * gs),
                    LocalMax = new Vec3((grid.Max.X + 0.5) * gs, (grid.Max.Y + 0.5) * gs, (grid.Max.Z + 0.5) * gs),
                    Label = "mirror", Spawned = false,
                };
                GridMirrorProducer.Register(grid, "de.s3 mirror via " + via, info);
                lines.Add(grid.EntityId.ToString(CultureInfo.InvariantCulture) + " (" + grid.GetBlocks().Count + " blocks, " + (grid.IsStatic ? "static" : "dynamic") + ", " + grid.GridSizeEnum + ")");
            }
            string line = "DE.S3 mirror: " + picked.Count + " grid(s) registered: " + string.Join("; ", lines.ToArray()) + " (via " + via + ")";
            DeLog.Info(line);
            var r = Ok(line);
            r["grids"] = (long)picked.Count;
            r["entityId"] = picked[0].EntityId;
            return r;
        }

        private static Dictionary<string, object> Remove(string why)
        {
            _probe = null;
            int n = CloseOurGrids(why);
            RemoveGps();
            string line = "DE.S3 remove: " + n + " grid(s) closed (EntityRemove follows on the next tick), GPS removed";
            DeLog.Info(line);
            return Ok(line);
        }

        private static Dictionary<string, object> Status()
        {
            Dictionary<string, object> d = GridMirrorProducer.Status();
            var r = Ok("DE.S3 status: streaming=" + d["streaming"] + " grids=" + GridMirrorProducer.GridCount + " c3Written=" + d["c3Written"] + " c4Written=" + d["c4Written"] +
                       " modelsLoaded=" + d["modelsLoaded"] + (d["waiting"] != null ? " waiting: " + d["waiting"] : ""));
            foreach (var kv in d) r[kv.Key] = kv.Value;
            return r;
        }

        private static Dictionary<string, object> Export()
        {
            List<ColourExportItem> items = GridMirrorProducer.ExportItems();
            if (items.Count == 0) return Fail("de.s3 export: no mirrored part with a loaded material (place a block and wait for its model first)");
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            MaterialExport.Request(items, Win32.TickCount64(), stamp);
            var paths = new List<object>();
            foreach (ColourExportItem it in items) if (it.PngPath != null) paths.Add(it.PngPath);
            string line = "DE.S3 colour export requested: " + paths.Count + " PNG(s) under " + MaterialExport.Directory +
                          " (MyRenderProxy.RenderColoredTextures; written by SE's render thread, 'DE.S3 colour export written' follows)";
            DeLog.Info(line);
            var r = Ok(line);
            r["png"] = paths;
            if (paths.Count > 0) r["exportPng"] = paths[0];
            return r;
        }

        private static Dictionary<string, object> Gps(IDictionary<string, object> args)
        {
            bool? on = S3Args.OnOff(args, "gps");
            if (!on.HasValue) return Fail("de.s3 gps: \"on\" or \"off\"");
            GridMirrorProducer.GpsRows = on.Value;
            string line = "DE.S3 GPS pixel rows " + (on.Value ? "ON: s3.gps {tick, qpc, px} every frame (S3.N2)" : "OFF");
            DeLog.Info(line);
            var r = Ok(line);
            r["grids"] = GridMirrorProducer.GridCount;
            return r;
        }

        /// <summary>
 /// S3.N3: SE's pixel (top-left, SE viewport pixels) of corner i of the placed grid's local box (bit 0 max x, bit 1
 /// max y, bit 2 max z), or of an SN point <c>{sn: [x, y, z]}</c>, through <c>MySector.MainCamera.ViewProjectionMatrix</c>
 /// and <c>Viewport</c> as S1's de.pixel (the camera of the last PrepareForDraw: tick = the last C1 tick).
 /// </summary>
        private static Dictionary<string, object> Pixel(IDictionary<string, object> args, string via)
        {
            SiteMap site;
            string why;
            if (!S1Commands.Ready(out site, out why)) return Fail("de.s3 pixel: " + why);
            var pa = MiniJson.GetObject(args, "pixel");
            string label = MiniJson.GetString(args, "label") ?? MiniJson.GetString(pa, "label") ?? "pixel";
            Vec3 pSe;
            int corner = -1;
            double? cn = MiniJson.GetNumber(pa, "corner");
            object snv;
            if (cn.HasValue)
            {
                corner = (int)cn.Value;
                if (corner < 0 || corner > 7) return Fail("de.s3 pixel: corner 0..7");
                MyCubeGrid g;
                S3GridInfo info;
                if (!GridMirrorProducer.Latest(out g, out info)) return Fail("de.s3 pixel: no S3 grid placed");
                pSe = FrameWriter.ToSe(g.WorldMatrix).Transform(S3Placement.Corner(info.LocalMin, info.LocalMax, corner));
            }
            else if (pa != null && pa.TryGetValue("sn", out snv) && snv is List<object> && ((List<object>)snv).Count == 3)
            {
                var l = (List<object>)snv;
                pSe = site.SnToSe(new Vec3(Convert.ToDouble(l[0], CultureInfo.InvariantCulture), Convert.ToDouble(l[1], CultureInfo.InvariantCulture),
                                           Convert.ToDouble(l[2], CultureInfo.InvariantCulture)));
            }
            else return Fail("de.s3 pixel needs {pixel: {corner: 0..7} | {sn: [x, y, z]}, label}");
            MyCamera cam = MySector.MainCamera;
            MatrixD vpm = cam.ViewProjectionMatrix;
            var vp = new[]
            {
                vpm.M11, vpm.M12, vpm.M13, vpm.M14, vpm.M21, vpm.M22, vpm.M23, vpm.M24,
                vpm.M31, vpm.M32, vpm.M33, vpm.M34, vpm.M41, vpm.M42, vpm.M43, vpm.M44,
            };
            var vport = cam.Viewport;
            double px, py, ndcZ;
            bool front = S1Math.Project(vp, vport.OffsetX, vport.OffsetY, vport.Width, vport.Height, pSe, out px, out py, out ndcZ);
            Vec3 pSn = site.SeToSn(pSe);
            SeMatrix cw = FrameWriter.ToSe(cam.WorldMatrix);
            FrameDiagnostics d = FrameWriter.LastDiagnostics;
            double vfov = WireMath.VerticalFovDegrees(cam.FovWithZoom);
            ulong tick = FrameWriter.LastTick;
            double s = site.ScaleAt(cw.Translation);
            string line = "DE.S3 SE pixel " + label + " tick=" + tick + (corner >= 0 ? " corner=" + corner : "") + " sn=" + V(pSn) + " px=(" + N(px) + ", " + N(py) +
                          ") inFront=" + front + " vFovDeg=" + N(vfov) + " camSn=" + V(d.CamSn) + " s=" + N(s) + " (S3.N3; via " + via + ")";
            DeLog.Info(line);
            var sb = new System.Text.StringBuilder(384);
            S3Rows.Head(sb, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), "pixel");
            sb.Append(",\"label\":"); JsonText.AppendString(sb, label);
            sb.Append(",\"tick\":").Append(tick.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"sePx\":[").Append(S3Rows.G(px)).Append(',').Append(S3Rows.G(py)).Append(']');
            sb.Append(",\"pointSe\":["); S3Rows.Vec(sb, pSe);
            sb.Append("],\"pointSn\":["); S3Rows.Vec(sb, pSn);
            sb.Append("],\"inFront\":").Append(front ? "true" : "false").Append('}');
            Jsonl.WriteLine(sb.ToString());
            var r = Ok(line);
            r["label"] = label;
            r["tick"] = (double)tick;
            r["sePx"] = new object[] { px, py };
            r["px"] = new object[] { px, py };
            r["inFront"] = front;
            r["pointSe"] = Arr(pSe);
            r["pointSn"] = Arr(pSn);
            r["camSn"] = Arr(d.CamSn);
            r["vFovDeg"] = vfov;
            r["s"] = s;
            r["screen"] = new object[] { (double)vport.Width, (double)vport.Height };
            if (corner >= 0) r["corner"] = corner;
            return r;
        }

        // ================================================================================================= de.motion yaw

 /// <summary>
 /// <c>de.motion {kind: "yaw", amplitudeDeg, hz, seconds, label}</c> (S3.N2): the SE spectator turns about SN up at
 /// its position, one sample per SE update before PrepareForDraw (the S2Motion pattern: <c>S1Commands.SetSpectator</c>,
 /// no teleport flag), then returns to its start heading. Other kinds go on to S2Motion (null); "stop" also stops a
 /// running yaw.
 /// </summary>
        private static Dictionary<string, object> Motion(IDictionary<string, object> args, string via)
        {
            string kind = MiniJson.GetString(args, "kind");
            if (kind == "stop")
            {
                if (_yaw != null) { DeLog.Info("DE.Motion stopped yaw " + _yaw.Label + " at update " + _yawK + "/" + _yaw.Ticks); _yaw = null; }
                return null; // S2Motion answers stop
            }
            if (kind != "yaw") return null;
            try
            {
                SiteMap site;
                string why;
                if (!S1Commands.Ready(out site, out why)) return Fail("de.motion yaw: " + why);
                if (S1Commands.PathActive || S2Motion.Active) return Fail("de.motion yaw: another motion (de.path or de.motion) is running");
                YawPlan plan = YawPlan.Parse(args, out why);
                if (plan == null) return Fail(why);
                SeMatrix cur = FrameWriter.ToSe(MySector.MainCamera.WorldMatrix);
                Vec3 e, u, n;
                site.FrameAt(cur.Translation, out e, out u, out n);
                _yawPosSe = cur.Translation;
                _yawFwdSn = site.DirSeToSn(e, u, n, cur.Forward);
                _yawUpSn = site.DirSeToSn(e, u, n, cur.Up);
                _yaw = plan;
                _yawK = 0;
                _yawStartTick = FrameWriter.LastTick + 1;
                string line = "DE.Motion start yaw " + plan.Label + ": " + N(plan.AmplitudeDeg) + " deg x sin(2 pi " + N(plan.Hz) + " Hz t) for " + N(plan.Seconds) +
                              " s about SN up at " + V(site.SeToSn(_yawPosSe)) + " (one sample per SE update from tick ~" + _yawStartTick + "; via " + via + ")";
                DeLog.Info(line);
                var r = Ok(line);
                r["startTick"] = (double)_yawStartTick;
                r["endTick"] = (double)(_yawStartTick + (ulong)plan.Ticks);
                return r;
            }
            catch (Exception ex) { return Fail("de.motion yaw: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static void TickYaw()
        {
            YawPlan plan = _yaw;
            if (plan == null) return;
            try
            {
                SiteMap site = SiteBinding.Map;
                if (site == null || MySector.MainCamera == null) { AbortYaw("the site is no longer bound"); return; }
                if (S1Commands.PathActive || S2Motion.Active) { AbortYaw("another motion started"); return; }
                double a = plan.AngleAt(_yawK);
                SeMatrix m = S1Math.SpectatorAtSe(site, _yawPosSe, S1Math.YawSn(_yawFwdSn, a), S1Math.YawSn(_yawUpSn, a));
                string why;
                if (!S1Commands.SetSpectator(m, out why)) { AbortYaw(why); return; }
                if (_yawK >= plan.Ticks)
                {
                    _yaw = null;
                    DeLog.Info("DE.Motion done yaw " + plan.Label + ": " + (plan.Ticks + 1) + " samples from tick ~" + _yawStartTick + " (back at the start heading)");
                    return;
                }
                _yawK++;
            }
            catch (Exception e) { AbortYaw(e.GetType().Name + ": " + e.Message); }
        }

        private static void AbortYaw(string why)
        {
            if (_yaw == null) return;
            DeLog.Info("DE.Motion aborted yaw " + _yaw.Label + " at update " + _yawK + "/" + _yaw.Ticks + ": " + why);
            _yaw = null;
        }

        // ================================================================================================= helpers

        private static Dictionary<string, object> Ok(string line)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", true }, { "line", line } };
        }

        private static Dictionary<string, object> Fail(string error)
        {
            DeLog.Info("DE.S3 " + error);
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", false }, { "error", error }, { "line", error } };
        }

        private static object[] Arr(Vec3 v) { return new object[] { v.X, v.Y, v.Z }; }
        private static string N(double v) { return v.ToString("0.######", CultureInfo.InvariantCulture); }
        private static string G(double v) { return v.ToString("G17", CultureInfo.InvariantCulture); }
        private static string V(Vec3 v) { return "(" + G(v.X) + "," + G(v.Y) + "," + G(v.Z) + ")"; }
    }
}
