using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    // =================================================================================================================
 // S7a placement ghost, the game-independent half (CONTRACT §5.4 "S7a conventions", "Placement ghost"; ROADMAP S7a
 // "Placement ghost"): the seeded failure S7a.M4, the switch, the record plan (which C4 records a tick needs), the
 // latency bookkeeping against C7 presentedSeTick, the s7a.ghost row and the de.s7 ghost syntax. No SE type here, so
 // tests/DeepEngineers.SE.Tests links this file; PlacementGhost.cs is the game side that feeds it.
 // =================================================================================================================

 /// <summary>
 /// Seeded failure of slice S7a owned by the placement ghost (ROADMAP S7a "Seeded failures"): S7a.M4 sets
 /// <c>ghostAllowed</c> on every ghost C2 row (S7a.N3 colour must turn red: green while SE shows the block blocked).
 /// Dev builds only, switched through <see cref="Mutants.Set"/> (<c>de.mutant S7a.M4 on|off</c>). The other S7a ids
 /// M2, M5, M6: Subnautica) and return null here.
 /// </summary>
    internal static class S7aGhostMutants
    {
        /// <summary>S7a.M4: <c>ghostAllowed</c> always set on the ghost row.</summary>
        public static volatile bool AllowedAlways;

        public static string Active { get { return AllowedAlways ? "S7a.M4" : ""; } }

        public static void ClearAll() { AllowedAlways = false; }

        /// <summary>Switches S7a.M4; returns the log text, or null for any other id.</summary>
        public static string Set(string id, bool on)
        {
            if (!string.Equals((id ?? "").Trim(), "S7a.M4", StringComparison.OrdinalIgnoreCase)) return null;
            AllowedAlways = on;
            return "S7a.M4 " + (on ? "ON" : "OFF") + " (the placement ghost's C2 row always carries ghostAllowed; S7a.N3 colour red: green while SE shows it blocked)";
        }
    }

    /// <summary>The ghost switch: env <c>DEEPENGINEERS_GHOST=on|off</c> (default on), <c>de.s7 {ghost: "on"|"off"}</c>.</summary>
    internal static class GhostSwitch
    {
        public const string EnvVar = "DEEPENGINEERS_GHOST";

        /// <summary>on/1/true/yes → true, off/0/false/no → false, anything else (or unset) → the default (on).</summary>
        public static bool Parse(string v, bool fallback)
        {
            if (string.IsNullOrEmpty(v)) return fallback;
            switch (v.Trim().ToLowerInvariant())
            {
                case "on": case "1": case "true": case "yes": return true;
                case "off": case "0": case "false": case "no": return false;
                default: return fallback;
            }
        }

        public static bool FromEnvironment()
        {
            try { return Parse(Environment.GetEnvironmentVariable(EnvVar), true); }
            catch (Exception) { return true; }
        }
    }

    /// <summary>One ghost part as the plan sends it (the block model part, or one cube part of an armor definition).</summary>
    internal struct GhostPartSpec
    {
        public ulong PartKey;
        public ulong ModelKey;
        /// <summary>The asset path the model loader reads (C3), and its scale (<c>|scale=</c> in the key when ≠ 1).</summary>
        public string AssetName;
        public float Scale;
        /// <summary>Block-frame SE rows Right, Up, Backward, Translation (12 floats).</summary>
        public float[] Local;
        /// <summary>The PartAdd flags (S7aGhost: FatBlock | NoShadows for the model part, CubePart | NoShadows otherwise).</summary>
        public uint Flags;

        public bool SameAs(GhostPartSpec o)
        {
            if (PartKey != o.PartKey || ModelKey != o.ModelKey || Flags != o.Flags) return false;
            if (Local == null || o.Local == null || Local.Length != 12 || o.Local.Length != 12) return false;
            for (int i = 0; i < 12; i++) if (Local[i] != o.Local[i]) return false;
            return true;
        }

        /// <summary>The PartAdd record of this spec (through Wire's S7aGhost builders).</summary>
        public PartAdd ToRecord()
        {
            if (Flags == MirrorConventions.PartFlags(MirrorPartClass.FatBlock, MirrorPartFlags.NoShadows) && PartKey == S7aGhost.ModelPartKey)
                return S7aGhost.MakeModelPart(ModelKey, new Vec3(Local[9], Local[10], Local[11]));
            return S7aGhost.MakeCubePart(PartKey, ModelKey, Local);
        }

        /// <summary>The model part of a definition with a block model: key 1, local = translation(ModelOffset).</summary>
        public static GhostPartSpec ModelPart(ulong modelKey, string asset, float scale, float offX, float offY, float offZ)
        {
            return new GhostPartSpec
            {
                PartKey = S7aGhost.ModelPartKey, ModelKey = modelKey, AssetName = asset, Scale = scale,
                Local = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, offX, offY, offZ },
                Flags = MirrorConventions.PartFlags(MirrorPartClass.FatBlock, MirrorPartFlags.NoShadows),
            };
        }

        /// <summary>A cube part (key 1…MaxParts) at the rows GetCubeParts returned.</summary>
        public static GhostPartSpec CubePart(ulong partKey, ulong modelKey, string asset, float scale, float[] rows)
        {
            return new GhostPartSpec
            {
                PartKey = partKey, ModelKey = modelKey, AssetName = asset, Scale = scale, Local = (float[])rows.Clone(),
                Flags = MirrorConventions.PartFlags(MirrorPartClass.CubePart, MirrorPartFlags.NoShadows),
            };
        }
    }

    internal enum GhostOpKind { GridAdd = 1, PartAdd = 2, PartRemove = 3, EntityRemove = 4 }

    /// <summary>One C4 record the plan asks for (lane 1: GridAdd, EntityRemove; lane 2: PartAdd, PartRemove).</summary>
    internal struct GhostOp
    {
        public GhostOpKind Kind;
        public uint TargetClass;
        public GhostPartSpec Part;
        public ulong PartKey;

        public override string ToString()
        {
            switch (Kind)
            {
                case GhostOpKind.GridAdd: return "GridAdd class " + TargetClass.ToString(CultureInfo.InvariantCulture);
                case GhostOpKind.PartAdd: return "PartAdd key " + Part.PartKey.ToString(CultureInfo.InvariantCulture) + " model 0x" + Part.ModelKey.ToString("x16", CultureInfo.InvariantCulture);
                case GhostOpKind.PartRemove: return "PartRemove key " + PartKey.ToString(CultureInfo.InvariantCulture);
                default: return "EntityRemove";
            }
        }
    }

    /// <summary>
 /// The ghost's record plan (CONTRACT §5.4 "Placement ghost"): GridAdd once per activation and again when the cube
 /// size changes; PartAdd whenever a part is new or differs (same key: a replace), PartRemove of keys no longer used
 /// (removes first); EntityRemove when it stops being active; a stream restart (ClearAll) forgets everything so the
 /// next active tick resends from its GridAdd. <see cref="PartsVersion"/> is the C2 row's part serial.
 /// </summary>
    internal sealed class GhostPlan
    {
        private readonly Dictionary<ulong, GhostPartSpec> _sent = new Dictionary<ulong, GhostPartSpec>();
        private readonly List<ulong> _scratch = new List<ulong>();

        /// <summary>A GridAdd was queued since the last removal or stream restart.</summary>
        public bool Alive { get; private set; }
        public uint SentClass { get; private set; }
        public uint PartsVersion { get; private set; }
        public int SentParts { get { return _sent.Count; } }
        public long GridAdds, PartAdds, PartRemoves, EntityRemoves, Activations;

        /// <summary>Active this tick with these parts (at most <see cref="S7aGhost.MaxParts"/>): the records to queue, in order.</summary>
        public void Active(uint targetClass, IList<GhostPartSpec> parts, List<GhostOp> ops)
        {
            if (!Alive)
            {
                Alive = true;
                SentClass = targetClass;
                _sent.Clear();
                Activations++;
                GridAdds++;
                ops.Add(new GhostOp { Kind = GhostOpKind.GridAdd, TargetClass = targetClass });
            }
            else if (targetClass != SentClass)
            {
                // the cube size changed: the same id again with its new class (Subnautica re-indexes the cells)
                SentClass = targetClass;
                GridAdds++;
                ops.Add(new GhostOp { Kind = GhostOpKind.GridAdd, TargetClass = targetClass });
            }
            bool changed = false;
            _scratch.Clear();
            foreach (ulong k in _sent.Keys)
            {
                bool kept = false;
                if (parts != null) for (int i = 0; i < parts.Count; i++) if (parts[i].PartKey == k) { kept = true; break; }
                if (!kept) _scratch.Add(k);
            }
            _scratch.Sort();
            foreach (ulong k in _scratch)
            {
                _sent.Remove(k);
                PartRemoves++;
                changed = true;
                ops.Add(new GhostOp { Kind = GhostOpKind.PartRemove, PartKey = k });
            }
            if (parts != null)
                for (int i = 0; i < parts.Count && i < S7aGhost.MaxParts; i++)
                {
                    GhostPartSpec p = parts[i];
                    GhostPartSpec old;
                    if (_sent.TryGetValue(p.PartKey, out old) && old.SameAs(p)) continue;
                    _sent[p.PartKey] = p;
                    PartAdds++;
                    changed = true;
                    ops.Add(new GhostOp { Kind = GhostOpKind.PartAdd, Part = p });
                }
            if (changed) PartsVersion++;
        }

        /// <summary>Not active this tick: EntityRemove once when it was alive.</summary>
        public void Inactive(List<GhostOp> ops)
        {
            if (!Alive) return;
            Alive = false;
            _sent.Clear();
            EntityRemoves++;
            PartsVersion++;
            ops.Add(new GhostOp { Kind = GhostOpKind.EntityRemove });
        }

        /// <summary>The stream restarted (ClearAll queued, C4 cleared): resent from its GridAdd at the next active tick.</summary>
        public void OnStreamRestart()
        {
            Alive = false;
            _sent.Clear();
        }

        /// <summary>The model keys of the parts sent (the audit's view).</summary>
        public IEnumerable<GhostPartSpec> Parts { get { return _sent.Values; } }
    }

    /// <summary>
 /// Latency bookkeeping (S7a.N3 "latency"): every SE tick whose ghost pose differs from the previous one is noted; it
 /// resolves at the first SE tick whose C7 <c>presentedSeTick</c> is at or past it (Subnautica presented a frame that
 /// used that pose or a newer one): latency = that SE tick − the change tick, in ticks. A newer change supersedes the
 /// pending ones it covers (one sample per presented frame). At most <see cref="MaxPending"/> pending (oldest dropped).
 /// </summary>
    internal sealed class GhostLatency
    {
        public const int MaxPending = 64;
        public const int Window = 256;

        private readonly Queue<ulong> _pending = new Queue<ulong>();
        private readonly long[] _window = new long[Window];
        private int _n, _next;

        public long Samples, Dropped, Last = -1, Max;

        public int Pending { get { return _pending.Count; } }

        public void NoteChange(ulong changeTick)
        {
            if (_pending.Count > 0)
            {
                ulong newest = 0;
                foreach (ulong t in _pending) newest = t;
                if (changeTick <= newest) return; // same tick (re-read) or a restart: kept as the older one
            }
            if (_pending.Count >= MaxPending) { _pending.Dequeue(); Dropped++; }
            _pending.Enqueue(changeTick);
        }

        /// <summary>The SE tick <paramref name="seTick"/> read C7 <paramref name="presentedSeTick"/>: resolved samples (0, 1 or more).</summary>
        public int Resolve(ulong seTick, ulong presentedSeTick, List<long> resolved)
        {
            int n = 0;
            while (_pending.Count > 0 && presentedSeTick != 0 && _pending.Peek() <= presentedSeTick)
            {
                ulong t = _pending.Dequeue();
                long lat = seTick >= t ? (long)(seTick - t) : 0;
                Last = lat;
                if (lat > Max) Max = lat;
                Samples++;
                _window[_next] = lat;
                _next = (_next + 1) % Window;
                if (_n < Window) _n++;
                if (resolved != null) resolved.Add(lat);
                n++;
            }
            return n;
        }

        /// <summary>The 95th percentile (nearest rank) of the last <see cref="Window"/> samples; -1 without samples.</summary>
        public long P95
        {
            get
            {
                if (_n == 0) return -1;
                var a = new long[_n];
                Array.Copy(_window, a, _n);
                Array.Sort(a);
                int rank = (int)Math.Ceiling(0.95 * _n) - 1;
                return a[Math.Max(0, Math.Min(_n - 1, rank))];
            }
        }

        public void Reset()
        {
            _pending.Clear();
        }
    }

    /// <summary>What one s7a.ghost row says (SE side; ROADMAP S7a "JSONL rows").</summary>
    internal struct GhostRowData
    {
        public ulong Tick;
        public bool Active, Shown, Allowed, Enabled;
        /// <summary>SE's own m_buildAllowed; differs from <see cref="Allowed"/> (the C2 flag) only under S7a.M4.</summary>
        public bool BuildAllowed;
        public string Def, Model, CubeSize, Mode, Reason;
        public ulong ModelKey;
        public int Parts;
        public uint PartsVersion;
        /// <summary>SE world matrix of the block frame (m_worldMatrixAdd), and the C2 pose it maps to.</summary>
        public SeMatrix SeWorld;
        public bool HasPose;
        public Vec3 SnO;
        public Quat SnQ;
        public double SnS;
        public long GridAdds, PartAdds, PartRemoves, EntityRemoves;
        public long LatencyTicks, LatencyP95, LatencyMax, LatencySamples;
        public ulong PresentedSeTick;
        public bool MutantAllowed;
    }

    /// <summary>The s7a.ghost row (SE): emitted on every change, and at 10 Hz while active.</summary>
    internal static class GhostRows
    {
        public const ulong ActiveRowMs = 100;

        /// <summary>The row text: {"t","ev":"s7a.ghost", tick, active, shown, allowed (the C2 flag), buildAllowed (m_buildAllowed), def, model, modelKey, cubeSize, mode, parts, seWorld {o, rows}, snPose {o, q, s}, c4 {...}, latency {...}}.</summary>
        public static string Row(string utc, GhostRowData d)
        {
            var sb = new StringBuilder(640);
            sb.Append("{\"t\":"); JsonText.AppendString(sb, utc);
            sb.Append(",\"ev\":\"s7a.ghost\"");
            S5Rows.Int(sb, "tick", (long)d.Tick);
            S5Rows.Hex(sb, "entityId", S7aGhost.EntityId);
            S5Rows.Bool(sb, "enabled", d.Enabled);
            S5Rows.Bool(sb, "active", d.Active);
            S5Rows.Bool(sb, "shown", d.Shown);
            S5Rows.Bool(sb, "allowed", d.Allowed);
            S5Rows.Bool(sb, "buildAllowed", d.BuildAllowed);
            if (d.MutantAllowed) S5Rows.Bool(sb, "mutantS7aM4", true);
            S5Rows.Str(sb, "def", d.Def);
            S5Rows.Str(sb, "model", d.Model);
            S5Rows.Hex(sb, "modelKey", d.ModelKey);
            S5Rows.Str(sb, "cubeSize", d.CubeSize);
            S5Rows.Str(sb, "mode", d.Mode);
            S5Rows.Int(sb, "parts", d.Parts);
            S5Rows.Int(sb, "partsVersion", d.PartsVersion);
            if (d.Reason != null) S5Rows.Str(sb, "reason", d.Reason);
            if (d.Active)
            {
                // rows: Right, Up, Backward, Translation (VRage row order), each [x, y, z]
                sb.Append(",\"seWorld\":{\"o\":["); S3Rows.Vec(sb, d.SeWorld.Translation); sb.Append("],\"rows\":[[");
                S3Rows.Vec(sb, d.SeWorld.Right); sb.Append("],["); S3Rows.Vec(sb, d.SeWorld.Up); sb.Append("],["); S3Rows.Vec(sb, d.SeWorld.Backward);
                sb.Append("],["); S3Rows.Vec(sb, d.SeWorld.Translation);
                sb.Append("]]}");
            }
            if (d.HasPose)
            {
                sb.Append(",\"snPose\":{\"o\":["); S3Rows.Vec(sb, d.SnO);
                sb.Append("],\"q\":[").Append(S3Rows.G(d.SnQ.X)).Append(',').Append(S3Rows.G(d.SnQ.Y)).Append(',').Append(S3Rows.G(d.SnQ.Z)).Append(',').Append(S3Rows.G(d.SnQ.W));
                sb.Append("],\"s\":").Append(S3Rows.G(d.SnS)).Append('}');
            }
            sb.Append(",\"c4\":{\"gridAdds\":").Append(d.GridAdds).Append(",\"partAdds\":").Append(d.PartAdds)
              .Append(",\"partRemoves\":").Append(d.PartRemoves).Append(",\"entityRemoves\":").Append(d.EntityRemoves).Append('}');
            sb.Append(",\"latency\":{\"lastTicks\":").Append(d.LatencyTicks).Append(",\"p95Ticks\":").Append(d.LatencyP95)
              .Append(",\"maxTicks\":").Append(d.LatencyMax).Append(",\"samples\":").Append(d.LatencySamples)
              .Append(",\"presentedSeTick\":").Append(d.PresentedSeTick.ToString(CultureInfo.InvariantCulture)).Append('}');
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>True when the row's state differs from the last one written (a change row is due at once).</summary>
        public static bool Changed(GhostRowData a, GhostRowData b)
        {
            return a.Active != b.Active || a.Shown != b.Shown || a.Allowed != b.Allowed || a.BuildAllowed != b.BuildAllowed || a.Enabled != b.Enabled || a.ModelKey != b.ModelKey ||
                   a.Parts != b.Parts || !string.Equals(a.Def, b.Def, StringComparison.Ordinal) || !string.Equals(a.Mode, b.Mode, StringComparison.Ordinal) ||
                   !string.Equals(a.CubeSize, b.CubeSize, StringComparison.Ordinal) || !string.Equals(a.Reason, b.Reason, StringComparison.Ordinal);
        }
    }

    /// <summary>de.s7 ghost syntax: <c>{ghost: "on"|"off"|"stop"}</c>, <c>{ghost: "probe", block: "&lt;TypeId&gt;/&lt;SubtypeId&gt;"}</c>, <c>{ghost: "status"}</c>.</summary>
    internal static class GhostArgs
    {
        public enum Verb { None, On, Off, Stop, Probe, Status, Bad }

        public static Verb Parse(IDictionary<string, object> args, out string block, out string error)
        {
            block = null;
            error = null;
            if (args == null) return Verb.None;
            object v;
            if (!args.TryGetValue("ghost", out v) || v == null) return Verb.None;
            if (v is bool) return (bool)v ? Verb.On : Verb.Off;
            string s = (v as string ?? "").Trim().ToLowerInvariant();
            switch (s)
            {
                case "on": return Verb.On;
                case "off": return Verb.Off;
                case "stop": return Verb.Stop;
                case "status": case "": return Verb.Status;
                case "probe":
                {
                    object b;
                    block = args.TryGetValue("block", out b) ? (b as string) : null;
                    if (string.IsNullOrEmpty(block) || block.IndexOf('/') <= 0 || block.IndexOf('/') == block.Length - 1)
                    {
                        error = "de.s7 ghost probe needs block \"<TypeId>/<SubtypeId>\" (e.g. \"CubeBlock/LargeBlockArmorBlock\")";
                        return Verb.Bad;
                    }
                    return Verb.Probe;
                }
                default:
                    error = "de.s7 ghost: unknown '" + s + "' (on, off, stop, probe, status)";
                    return Verb.Bad;
            }
        }

        /// <summary>The type id as SE parses it: "MyObjectBuilder_" is added when missing.</summary>
        public static string FullTypeId(string typeId)
        {
            if (string.IsNullOrEmpty(typeId)) return typeId;
            typeId = typeId.Trim();
            return typeId.StartsWith("MyObjectBuilder_", StringComparison.Ordinal) ? typeId : "MyObjectBuilder_" + typeId;
        }
    }
}
