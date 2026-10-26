using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The C2 subpart rows of S5 (CONTRACT §5.4 "C2 subpart table (S5 rows)", §2.8; D8): every tick, in the PrepareForDraw
 /// postfix right before C1, (1) one row per mirrored subgrid: <c>ownerId</c> = its hierarchy root, <c>partKey</c> = the
 /// subgrid's entity id, <c>local</c> = subWorld × rootWorld⁻¹ composed in doubles in SE space (VRage row order); (2) one
 /// row per dynamic subpart: <c>ownerId</c> = the grid (or subgrid) owning the block, <c>partKey</c> = the subpart key,
 /// <c>local</c> = its composed grid-local matrix (<see cref="GridExporter.Compose"/>); (3) the held tool
 /// (<see cref="CharacterExport.ToolRow"/>) and, S7p-scene-above (P6b), its own subparts (<see cref="ToolSubparts"/>);
 /// a render-thread spinner's row carries its integrated spin (<see cref="SubpartSpin"/>). Order: subgrids by root
 /// distance, dynamic subparts nearest first, the tool;
 /// rows past 2,048 are dropped and counted (<c>c2Dropped</c>). While traced (<c>de.s5 trace</c>) every row is logged as
 /// s5.sub with the SE matrices it came from (S5.N3). Only rows of entities whose GridAdd was written are emitted.
 /// Allocation-free per frame except the trace rows.
 /// </summary>
    internal static class SubgridPoses
    {
        private static readonly List<C2RowCandidate> _cands = new List<C2RowCandidate>(256);

        public static long Built { get; private set; }

        /// <summary>The rows of this frame into <paramref name="output"/>; returns the count, <paramref name="dropped"/> the overflow.</summary>
        public static int Build(Dictionary<long, GridMirrorProducer.MirroredGrid> grids, C4Lanes c4, Vec3 cam, SeSubpart[] output, out int dropped, bool trace, ulong tick)
        {
            _cands.Clear();
            SubpartSpin.BeginBuild(); // S7p-scene-above (P6b): the spin clock of this build
            foreach (GridMirrorProducer.MirroredGrid g in grids.Values)
            {
                if (g.GridAddIndex < 0 || !c4.IsDone(g.GridAddIndex) || g.Grid == null || g.Grid.Closed) continue;
                if (g.Subgrid)
                {
                    GridMirrorProducer.MirroredGrid root;
                    if (!grids.TryGetValue(g.ParentId, out root) || root.Grid == null || root.GridAddIndex < 0 || !c4.IsDone(root.GridAddIndex)) continue;
                    SeMatrix subW = FrameWriter.ToSe(g.Grid.WorldMatrix), rootW = FrameWriter.ToSe(root.Grid.WorldMatrix);
                    SeMatrix rel = InstanceMath.Relative(subW, rootW);
                    var c = new C2RowCandidate { Kind = C2RowKind.Subgrid, Distance = root.Distance, Row = InstanceMath.MakeSubpartRow((ulong)root.Id, (ulong)g.Id, rel) };
                    _cands.Add(c);
                    if (trace) Trace(tick, "subgrid", root.Id, (ulong)g.Id, c.Row, subW, rootW, rel);
                }
                if (g.S5 == null) continue;
                if (g.Impostor) continue; // S7t-se-composite (T3): an impostor carries no subparts (held back), so no subpart rows
                List<SubRef> dyn = g.S5.Dynamic;
                for (int i = 0; i < dyn.Count; i++)
                {
                    SubRef r = dyn[i];
                    SeMatrix comp;
                    if (!GridExporter.Compose(r, out comp)) continue;
                    SubpartSpin.Apply(g.Id, r, ref comp); // S7p-scene-above (P6b): SE spins drill heads, grinder blades, propellers, fans in the render thread only
                    var c = new C2RowCandidate { Kind = C2RowKind.DynamicSubpart, Distance = g.Distance, Row = InstanceMath.MakeSubpartRow((ulong)g.Id, r.Key, comp) };
                    _cands.Add(c);
                    if (trace) Trace(tick, "subpart", g.Id, r.Key, c.Row, FrameWriter.ToSe(r.Sub.WorldMatrix), FrameWriter.ToSe(g.Grid.WorldMatrix), comp);
                }
            }
            SeSubpart tool;
            if (CharacterExport.ToolRow(out tool))
            {
                _cands.Add(new C2RowCandidate { Kind = C2RowKind.Tool, Distance = 0, Row = tool });
                MySession ss = MySession.Static;
                ToolSubparts.AppendRows(_cands, ss != null ? ss.LocalCharacter : null); // S7p-scene-above (P6b): the held tool's own subparts (the drill's spike)
            }
            Built++;
            return C2RowPlanner.Plan(_cands, output, S5Defaults.MaxSubpartRows, out dropped);
        }

        /// <summary>sub's world matrix relative to root's: subWorld × rootWorld⁻¹ in doubles (SE space; Wire's InstanceMath.Relative, the root rigid).</summary>
        public static SeMatrix Relative(MyEntity sub, MyEntity root)
        {
            return InstanceMath.Relative(FrameWriter.ToSe(sub.WorldMatrix), FrameWriter.ToSe(root.WorldMatrix));
        }

        private static void Trace(ulong tick, string kind, long ownerId, ulong key, SeSubpart row, SeMatrix childWorld, SeMatrix ownerWorld, SeMatrix composed)
        {
            var sb = S5Rows.Head(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), "sub");
            S5Rows.Str(sb, "kind", kind);
            S5Rows.Int(sb, "tick", (long)tick);
            S5Rows.Id(sb, "ownerId", ownerId);
            S5Rows.Hex(sb, "partKey", key);
            var f = new float[12];
            for (int i = 0; i < 12; i++) f[i] = row.local[i];
            S5Rows.Floats(sb, "local", f);
            S5Rows.Matrix(sb, "localD", composed);
            S5Rows.Matrix(sb, "childWorld", childWorld);
            S5Rows.Matrix(sb, "ownerWorld", ownerWorld);
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
        }
    }
}
