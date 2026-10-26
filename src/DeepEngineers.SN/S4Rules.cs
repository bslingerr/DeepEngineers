using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeepEngineers.SN
{
    /// <summary>One requested column in SN world metres; <see cref="YHint"/> is NaN when the request gave none.</summary>
    internal sealed class S4Column
    {
        public int I;
        public double X, Z;
        public double YHint = double.NaN;
        public bool HasHint { get { return !double.IsNaN(YHint); } }
    }

    /// <summary>The options of one de.s4 probe batch (defaults: the S4 contract table; limits: <see cref="S4Rules"/>).</summary>
    internal sealed class S4Options
    {
        public bool Hold = true;
        public double FromY = S4Rules.DefaultFromY;
        public double HoldAbove = S4Rules.DefaultHoldAbove;
        public double Descend = S4Rules.DefaultDescend;
        public double MinY = S4Rules.DefaultMinY;
        public double HintSpan = S4Rules.DefaultHintSpan;
        public ulong TimeoutMs = S4Rules.DefaultTimeoutMs;
        public bool Mesh = true;
        public string Id;

        public string Describe()
        {
            return "hold=" + S4Rules.B(Hold) + " mesh=" + S4Rules.B(Mesh) + " fromY=" + S4Rules.F(FromY) + " holdAbove=" + S4Rules.F(HoldAbove) +
                   " descend=" + S4Rules.F(Descend) + " minY=" + S4Rules.F(MinY) + " hintSpan=" + S4Rules.F(HintSpan) +
                   " timeoutMs=" + TimeoutMs.ToString(CultureInfo.InvariantCulture);
        }
    }

    internal enum S4Kind { Probe, Status, Cancel, Teardown }

    /// <summary>A parsed de.s4 request.</summary>
    internal sealed class S4Request
    {
        public S4Kind Kind;
        public List<S4Column> Columns;
        public S4Options Options;
        public int From;
        public int Max = S4Rules.DefaultStatusMax;
        /// <summary>Status: list every column from <see cref="From"/> with its state (pending, holding, cancelled too), not only the finished ones.</summary>
        public bool All;
    }

    /// <summary>What one frame of a camera hold decides (<see cref="S4Rules.Decide"/>).</summary>
    internal enum S4Step
    {
        /// <summary>Keep holding: cast again next frame.</summary>
        Wait,
        /// <summary>The hit is the column's result, and the collision clipmap was built over it (verified).</summary>
        Accept,
        /// <summary>The hit is the column's result, but the clipmap never reported it built within the hold (verified = false).</summary>
        AcceptUnverified,
        /// <summary>Nothing in this hold's box: hold the camera <c>descend</c> metres lower.</summary>
        NextRung,
        /// <summary>The ladder is exhausted without a hit: the column failed.</summary>
        Fail,
    }

    /// <summary>One column's result, as the status reply, the log line and the JSONL row carry it.</summary>
    internal sealed class S4ColumnResult
    {
        public int I;
        public double X, Z;
        public double YHint = double.NaN;
        /// <summary>pending | holding | ok | failed | cancelled.</summary>
        public string State = "pending";
        public bool Hit, Verified;
        public double Y = double.NaN, CamY = double.NaN, OriginY = double.NaN;
        public string Collider;
        public int Layer = -1;
        public int Rungs;
        public long WaitMs = -1;
        public double RenderY = double.NaN, RenderFirstY = double.NaN;
        public int RenderHits, RenderUp, RenderDown, RenderFilters;
        public bool RenderVisible;
        public string RenderVia, RenderNote, Error;

        /// <summary>Render minus collider at the column (the native budget sample); NaN without both.</summary>
        public double RenderDelta { get { return Hit && !double.IsNaN(RenderY) && !double.IsNaN(Y) ? RenderY - Y : double.NaN; } }

        public bool Finished { get { return State == "ok" || State == "failed" || State == "cancelled"; } }

        public void WriteJson(JsonOut j)
        {
            j.Num("i", (long)I).Num("x", X).Num("z", Z).Num("yHint", YHint).Str("state", State).Bool("hit", Hit).Num("y", Y)
             .Str("collider", Collider).Num("layer", Layer >= 0 ? (double)Layer : double.NaN).Num("camY", CamY).Num("originY", OriginY)
             .Num("rungs", (long)Rungs).Num("waitMs", WaitMs >= 0 ? (double)WaitMs : double.NaN).Bool("verified", Verified)
             .Num("renderY", RenderY).Num("renderDelta", RenderDelta).Num("renderFirstY", RenderFirstY).Num("renderHits", (long)RenderHits)
             .Num("renderUp", (long)RenderUp).Num("renderDown", (long)RenderDown).Num("renderFilters", (long)RenderFilters)
             .Bool("renderVisible", RenderVisible).Str("renderVia", RenderVia).Str("renderNote", RenderNote).Str("error", Error);
        }

        public string ToJson()
        {
            var j = new JsonOut();
            WriteJson(j);
            return j.ToString();
        }
    }

    /// <summary>
 /// The render pass's choice among the vertical ray's crossings of the chunk's render triangles: the crossing nearest
 /// to the collider hit (the same surface: <c>renderY</c>, the native collider-vs-render deviation) and the first
 /// crossing below the ray origin (<c>renderFirstY</c>, what a viewer above sees). Crossings above the origin are
 /// ignored, as the physics ray never sees them.
 /// </summary>
    internal sealed class S4RenderPick
    {
        public double OriginY, ColliderY;
        public int Hits, Up, Down;
        public double FirstY = double.NaN, NearestY = double.NaN;
        public bool NearestUp;
        private double _nearestD = double.PositiveInfinity;

        public void Reset(double originY, double colliderY)
        {
            OriginY = originY;
            ColliderY = colliderY;
            Hits = Up = Down = 0;
            FirstY = NearestY = double.NaN;
            NearestUp = false;
            _nearestD = double.PositiveInfinity;
        }

        public void Add(double y, bool up)
        {
            if (double.IsNaN(y) || y > OriginY + 1e-6) return;
            Hits++;
            if (up) Up++; else Down++;
            if (double.IsNaN(FirstY) || y > FirstY) FirstY = y;
            double d = Math.Abs(y - ColliderY);
            if (d < _nearestD || (d == _nearestD && up && !NearestUp))
            {
                _nearestD = d;
                NearestY = y;
                NearestUp = up;
            }
        }

        /// <summary>One render triangle in world metres.</summary>
        public void AddTriangle(double px, double pz, double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz)
        {
            double y;
            bool up;
            if (S4Rules.VerticalHit(px, pz, ax, ay, az, bx, by, bz, cx, cy, cz, out y, out up)) Add(y, up);
        }
    }

    /// <summary>
 /// One de.s4 probe batch: its columns, their results in request order (processed one after the other, so the finished
 /// ones are the prefix [0, Done)), the batch state and its C11 replies, log lines and JSONL rows.
 /// </summary>
    internal sealed class S4Batch
    {
        public readonly string Id;
        public readonly S4Options Options;
        public readonly List<S4Column> Columns;
        public readonly S4ColumnResult[] Results;
        /// <summary>running | done | cancelled | failed.</summary>
        public string State = "running";
        public string Error;
        /// <summary>The next column to start (== Done between columns).</summary>
        public int Next;
        public int Done, Hits, VerifiedHits, RenderMeasured, Failed;
        public readonly ulong StartMs;
        public ulong EndMs;
        public bool OverrideWasActive;

        public S4Batch(string id, S4Options options, List<S4Column> columns, ulong now)
        {
            Id = id;
            Options = options;
            Columns = columns;
            StartMs = now;
            Results = new S4ColumnResult[columns.Count];
            for (int k = 0; k < columns.Count; k++)
            {
                S4Column c = columns[k];
                Results[k] = new S4ColumnResult { I = c.I, X = c.X, Z = c.Z, YHint = c.YHint };
            }
        }

        public bool Running { get { return State == "running"; } }

        /// <summary>Columns not run yet or in progress (cancelled ones are not pending).</summary>
        public int Pending
        {
            get
            {
                int n = 0;
                foreach (S4ColumnResult r in Results) if (r.State == "pending" || r.State == "holding") n++;
                return n;
            }
        }

        /// <summary>Counts a finished column (ok or failed) and moves to the next one.</summary>
        public void Complete(S4ColumnResult r)
        {
            Done++;
            Next = Done;
            if (r.Hit) Hits++;
            if (r.Hit && r.Verified) VerifiedHits++;
            if (!double.IsNaN(r.RenderY)) RenderMeasured++;
            if (r.State == "failed") Failed++;
        }

        /// <summary>Ends the batch (done, cancelled or failed); the unfinished columns become cancelled.</summary>
        public void End(string state, string error, ulong now)
        {
            State = state;
            Error = error;
            EndMs = now;
            for (int k = Done; k < Results.Length; k++)
            {
                S4ColumnResult r = Results[k];
                if (r.Finished) continue;
                r.State = "cancelled";
                if (r.Error == null) r.Error = state == "done" ? "not run" : "batch " + state + (error != null ? ": " + error : "");
            }
        }

        public void WriteStart(JsonObjectWriter w)
        {
            w.Bool("ok", true).Str("cmd", S4Rules.Cmd).Bool("pending", true).Str("batch", Id).Num("columns", (long)Columns.Count)
             .Bool("hold", Options.Hold).Bool("mesh", Options.Mesh).Bool("overrideWasActive", OverrideWasActive)
             .Str("note", "results: de.s4 {\"status\": true, \"from\": k}; log lines DE.S4 PROBE / DE.S4 BATCH; JSONL rows s4.probe / s4.batch");
        }

        /// <summary>
 /// The status reply: the finished columns from <paramref name="from"/> (with <paramref name="all"/> every column,
 /// pending/holding/cancelled included; at most <paramref name="max"/>, and at most <see cref="S4Rules.MaxReplyChars"/>
 /// characters of results so the reply fits C11d), "next" = the index to ask for next, "pending" = the columns not
 /// finished yet, and the column in progress as "current".
 /// </summary>
        public void WriteStatus(JsonObjectWriter w, int from, int max, ulong now, string current, bool all = false)
        {
            w.Bool("ok", true).Str("cmd", S4Rules.Cmd).Str("batch", Id).Str("state", State).Num("done", (long)Done).Num("columns", (long)Columns.Count)
             .Num("from", (long)from).Num("hits", (long)Hits).Num("verifiedHits", (long)VerifiedHits).Num("renderMeasured", (long)RenderMeasured)
             .Num("failed", (long)Failed).Num("pending", (long)Pending).Num("elapsedMs", (long)((Running ? now : EndMs) - StartMs))
             .Str("options", Options.Describe());
            if (Error != null) w.Str("batchError", Error);
            int next;
            bool truncated;
            int n;
            int end = all ? Columns.Count : Done;
            string results = ResultsJson(from, end, max, S4Rules.MaxReplyChars, out n, out next, out truncated);
            w.Raw("results", results).Num("returned", (long)n).Num("next", (long)next).Bool("more", next < end || (Running && !all)).Bool("truncated", truncated)
             .Bool("all", all);
            w.Raw("current", current ?? "null");
        }

        /// <summary>The results [from, end) as a JSON array (end = Done: the finished ones), bounded by <paramref name="max"/> entries and <paramref name="maxChars"/>.</summary>
        public string ResultsJson(int from, int end, int max, int maxChars, out int count, out int next, out bool truncated)
        {
            var sb = new StringBuilder(256);
            sb.Append('[');
            count = 0;
            truncated = false;
            if (from < 0) from = 0;
            if (end > Results.Length) end = Results.Length;
            next = Math.Min(from, end);
            for (int k = next; k < end && count < max; k++)
            {
                string e = Results[k].ToJson();
                if (count > 0 && sb.Length + e.Length + 2 > maxChars) { truncated = true; break; }
                if (count > 0) sb.Append(',');
                sb.Append(e);
                count++;
                next = k + 1;
            }
            sb.Append(']');
            return sb.ToString();
        }

        public string StartLine()
        {
            return "DE.S4 BATCH " + Id + " start n=" + Columns.Count + " " + Options.Describe() + " overrideWasActive=" + S4Rules.B(OverrideWasActive) +
                   " (layer-30 TerrainCollider rays after Physics.SyncTransforms, the S1 camera override held near each column)";
        }

        /// <summary>"DE.S4 BATCH &lt;id&gt; done|cancelled|failed n=… hits=… …" (the S4 contract's batch line).</summary>
        public string EndLine()
        {
            return "DE.S4 BATCH " + Id + " " + State + " n=" + Columns.Count + " hits=" + Hits + " verified=" + VerifiedHits +
                   " renderMeasured=" + RenderMeasured + " failed=" + Failed + " done=" + Done + " ms=" + (EndMs - StartMs).ToString(CultureInfo.InvariantCulture) +
                   (Error != null ? " error=" + S4Rules.Token(Error) : "");
        }

        /// <summary>The structured JSONL row "s4.batch" (start or end).</summary>
        public string BatchRow(string phase, string utc, ulong tickMs, ulong frame)
        {
            var j = new JsonOut();
            S4Rules.RowHead(j, "s4.batch", utc, tickMs, frame);
            j.Obj("data").Str("batch", Id).Str("phase", phase).Str("state", State).Num("columns", (long)Columns.Count).Num("done", (long)Done)
             .Num("hits", (long)Hits).Num("verifiedHits", (long)VerifiedHits).Num("renderMeasured", (long)RenderMeasured).Num("failed", (long)Failed)
             .Bool("hold", Options.Hold).Bool("mesh", Options.Mesh).Num("fromY", Options.FromY).Num("holdAbove", Options.HoldAbove)
             .Num("descend", Options.Descend).Num("minY", Options.MinY).Num("hintSpan", Options.HintSpan).Num("timeoutMs", (long)Options.TimeoutMs)
             .Bool("overrideWasActive", OverrideWasActive).Str("error", Error).EndObj();
            return j.ToString();
        }
    }

    /// <summary>
 /// The S4 raycast service's rules (ROADMAP S4 "SN plugin"; the S4 contract result §3): request grammar, the camera hold
 /// ladder, the per-frame hold decision, the vertical ray/triangle test, the log line and the JSONL row. Unity-free;
 /// the Unity half is <see cref="S4Host"/>.
 /// <para>
 /// The ladder: Subnautica builds terrain colliders (layer 30, the collision clipmap level) only within about ±16 m of
 /// its camera (S1.N3), so each column is probed with the S1 camera override held at a "rung": with a height hint at
 /// <c>yHint + holdAbove</c>, else at <c>fromY</c>; while a rung's box (camera ±16 m) is built and still empty, or the
 /// rung times out, the camera is held <c>descend</c> metres lower (≤ 32 m, so successive boxes overlap), down to
 /// <c>minY</c> (or <c>yHint − hintSpan</c> with a hint). Every rung casts from the same origin, 16 m above the first
 /// rung's camera, so the result is the first terrain surface from above, as SE's CastRay from above sees it.
 /// </para>
 /// </summary>
    internal static class S4Rules
    {
        public const string Cmd = "de.s4";

        /// <summary>Half-size of the camera box in which the collision clipmap answers "built" (S1.N3, S1Telemetry.BoxHalf).</summary>
        public const double BoxHalf = 16.0;
        /// <summary>Consecutive frames the box must read "built" before a hit is accepted or an empty box left.</summary>
        public const int MinBuiltFrames = 2;
        /// <summary>A hit this far below the box bottom still counts as inside it (float slack).</summary>
        public const double BoxSlack = 0.5;

        public const int MaxColumns = 4096;
        public const int DefaultStatusMax = 64, MaxStatusMax = 512;
        /// <summary>Results characters per status reply (C11d carries at most 128 KiB per message).</summary>
        public const int MaxReplyChars = 60000;

        public const double DefaultFromY = -5, DefaultHoldAbove = 8, DefaultDescend = 24, DefaultMinY = -1950, DefaultHintSpan = 64;
        public const ulong DefaultTimeoutMs = 6000;

        /// <summary>The compiled-octree world (SnOctreeFormat): x, z in [-2048, 2048], y in [-3040, 160].</summary>
        public const double WorldMinXZ = -2048, WorldMaxXZ = 2048, WorldMinY = -3040, WorldMaxY = 160;
        /// <summary>
 /// S8a window universe: batch ids bx, bz in [UniverseMin, UniverseMax] (CONTRACT §3.3 "Window"),
 /// SN x, z in [OriginX + UniverseMin·160, OriginX + (UniverseMax + 1)·160) = [−7,648, 7,712). Probes reach it while the
 /// window is served (<see cref="WindowUniverse"/>, set by S8aTerrain from the header's WindowOn and the loader prefix).
 /// </summary>
        public const double WindowMinXZ = -2048 - 35 * 160, WindowMaxXZ = -2048 + 61 * 160;
        /// <summary>True while Subnautica serves the S8a window (the probe bound widens to the window universe).</summary>
        public static volatile bool WindowUniverse;
        public const double MaxFromY = 400;
        public const ulong MinTimeoutMs = 100, MaxTimeoutMs = 600000;

        private static readonly string[] Keys =
        {
            "action", "probe", "columns", "hold", "fromY", "holdAbove", "descend", "minY", "hintSpan", "timeoutMs", "mesh", "id",
            "status", "from", "max", "all", "cancel", "teardown",
        };

        public const string Usage = "de.s4 {probe: [[x, z] | [x, z, yHint], ...], hold?: true, fromY?: -5, holdAbove?: 8, descend?: 24, minY?: -1950, " +
                                    "hintSpan?: 64, timeoutMs?: 6000, mesh?: true, id?} | {status: true, from?: 0, max?: 64, all?: false} | {cancel: true} | {teardown: true}";

        // ------------------------------------------------------------------------------------------ request grammar

        public static bool TryParse(object args, out S4Request req, out string error)
        {
            req = null;
            error = null;
            Dictionary<string, object> a = S3Args.ToDict(args);
            foreach (string k in a.Keys)
            {
                if (Array.IndexOf(Keys, k) < 0)
                {
                    error = "unknown key '" + k + "' (" + Usage + ")";
                    return false;
                }
            }
            object pv;
            bool hasProbe = a.TryGetValue("probe", out pv) || a.TryGetValue("columns", out pv);
            if (a.ContainsKey("probe") && a.ContainsKey("columns")) { error = "probe and columns are the same list: give one"; return false; }
            bool cancel = S3Args.Flag(a, "cancel", false), teardown = S3Args.Flag(a, "teardown", false), status = S3Args.Flag(a, "status", false);
            string action = S3Args.Str(a, "action", null);
            S4Kind kind;
            int kinds = (hasProbe ? 1 : 0) + (cancel ? 1 : 0) + (teardown ? 1 : 0) + (status ? 1 : 0);
            if (action != null)
            {
                switch (action)
                {
                    case "probe": kind = S4Kind.Probe; break;
                    case "status": kind = S4Kind.Status; break;
                    case "cancel": kind = S4Kind.Cancel; break;
                    case "teardown": kind = S4Kind.Teardown; break;
                    default: error = "action: probe | status | cancel | teardown"; return false;
                }
                if (kind == S4Kind.Probe && !hasProbe) { error = "action probe needs probe: [[x, z], ...]"; return false; }
            }
            else
            {
                if (kinds > 1) { error = "give one of probe, status, cancel, teardown"; return false; }
                kind = hasProbe ? S4Kind.Probe : cancel ? S4Kind.Cancel : teardown ? S4Kind.Teardown : S4Kind.Status;
            }
            req = new S4Request { Kind = kind };
            if (kind == S4Kind.Status)
            {
                double from = S3Args.Num(a, "from", 0), max = S3Args.Num(a, "max", DefaultStatusMax);
                if (from < 0 || from != Math.Floor(from) || from > MaxColumns) { error = "from: an integer in [0, " + MaxColumns + "]"; req = null; return false; }
                if (max < 1 || max != Math.Floor(max) || max > MaxStatusMax) { error = "max: an integer in [1, " + MaxStatusMax + "]"; req = null; return false; }
                object av;
                bool all = false;
                if (a.TryGetValue("all", out av) && !S3Args.TryBool(av, out all)) { error = "all: true | false"; req = null; return false; }
                req.From = (int)from;
                req.Max = (int)max;
                req.All = all;
                return true;
            }
            if (kind != S4Kind.Probe) return true;

            List<S4Column> cols;
            if (!TryColumns(pv, out cols, out error)) { req = null; return false; }
            S4Options o;
            if (!TryOptions(a, out o, out error)) { req = null; return false; }
            req.Columns = cols;
            req.Options = o;
            return true;
        }

        /// <summary>[[x, z] | [x, z, yHint] | {x, z, y|yHint},...], a single [x, z(yHint)], or "x,z[,y];x,z[,y]".</summary>
        public static bool TryColumns(object v, out List<S4Column> cols, out string error)
        {
            cols = new List<S4Column>();
            error = null;
            var list = v as List<object>;
            var s = v as string;
            if (s != null)
            {
                list = new List<object>();
                foreach (string part in s.Split(';'))
                {
                    if (part.Trim().Length == 0) continue;
                    var nums = new List<object>();
                    foreach (string t in part.Split(',')) nums.Add(t.Trim());
                    list.Add(nums);
                }
            }
            if (list == null) { error = "probe: a list of columns [[x, z] | [x, z, yHint], ...] (SN world m)"; return false; }
            bool single = list.Count >= 2 && list.Count <= 3;
            foreach (object o in list) if (o is List<object> || o is Dictionary<string, object>) { single = false; break; }
            if (single) list = new List<object> { list };
            if (list.Count == 0) { error = "probe: no column"; return false; }
            if (list.Count > MaxColumns) { error = "probe: " + list.Count + " columns, at most " + MaxColumns + " per batch"; return false; }
            for (int i = 0; i < list.Count; i++)
            {
                double x, z, y = double.NaN;
                if (!TryColumn(list[i], out x, out z, out y))
                {
                    error = "probe[" + i + "]: [x, z] or [x, z, yHint] numbers (or {x, z, y})";
                    return false;
                }
                double lo = WindowUniverse ? WindowMinXZ : WorldMinXZ, hi = WindowUniverse ? WindowMaxXZ : WorldMaxXZ;
                if (x < lo || x > hi || z < lo || z > hi)
                {
                    error = "probe[" + i + "]: x and z must lie in [" + F(lo) + ", " + F(hi) + "] (" + (WindowUniverse ? "the S8a window universe" : "Subnautica's octree world; the S8a window is not served") + ")";
                    return false;
                }
                if (!double.IsNaN(y) && (y < WorldMinY || y > WorldMaxY))
                {
                    error = "probe[" + i + "]: yHint must lie in [" + F(WorldMinY) + ", " + F(WorldMaxY) + "]";
                    return false;
                }
                cols.Add(new S4Column { I = i, X = x, Z = z, YHint = y });
            }
            return true;
        }

        private static bool TryColumn(object v, out double x, out double z, out double y)
        {
            x = z = y = double.NaN;
            var l = v as List<object>;
            if (l != null)
            {
                if (l.Count < 2 || l.Count > 3) return false;
                if (!S1Args.TryDouble(l[0], out x) || !S1Args.TryDouble(l[1], out z)) return false;
                if (l.Count == 3 && l[2] != null && !S1Args.TryDouble(l[2], out y)) return false;
                return true;
            }
            var d = v as Dictionary<string, object>;
            if (d == null) return false;
            object ox, oz, oy;
            if (!d.TryGetValue("x", out ox) || !d.TryGetValue("z", out oz) || !S1Args.TryDouble(ox, out x) || !S1Args.TryDouble(oz, out z)) return false;
            if ((d.TryGetValue("yHint", out oy) || d.TryGetValue("y", out oy)) && oy != null && !S1Args.TryDouble(oy, out y)) return false;
            return true;
        }

        private static bool TryOptions(Dictionary<string, object> a, out S4Options o, out string error)
        {
            o = new S4Options();
            error = null;
            object v;
            bool b;
            if (a.TryGetValue("hold", out v)) { if (!S3Args.TryBool(v, out b)) { error = "hold: true | false"; return false; } o.Hold = b; }
            if (a.TryGetValue("mesh", out v)) { if (!S3Args.TryBool(v, out b)) { error = "mesh: true | false"; return false; } o.Mesh = b; }
            if (!Num(a, "fromY", WorldMinY, MaxFromY, ref o.FromY, out error)) return false;
            if (!Num(a, "holdAbove", 0, BoxHalf, ref o.HoldAbove, out error)) return false;
            if (!Num(a, "descend", 1, 2 * BoxHalf, ref o.Descend, out error)) return false;
            if (!Num(a, "minY", WorldMinY, MaxFromY, ref o.MinY, out error)) return false;
            if (!Num(a, "hintSpan", 0, WorldMaxY - WorldMinY, ref o.HintSpan, out error)) return false;
            double t = o.TimeoutMs;
            if (!Num(a, "timeoutMs", MinTimeoutMs, MaxTimeoutMs, ref t, out error)) return false;
            o.TimeoutMs = (ulong)Math.Round(t);
            if (o.MinY > o.FromY) { error = "minY (" + F(o.MinY) + ") must not lie above fromY (" + F(o.FromY) + ")"; return false; }
            if (a.TryGetValue("id", out v) && v != null)
            {
                string id;
                if (!TryId(v, out id, out error)) return false;
                o.Id = id;
            }
            return true;
        }

        private static bool Num(Dictionary<string, object> a, string key, double lo, double hi, ref double value, out string error)
        {
            error = null;
            object v;
            if (!a.TryGetValue(key, out v)) return true;
            double d;
            if (!S1Args.TryDouble(v, out d) || d < lo || d > hi)
            {
                error = key + ": a number in [" + F(lo) + ", " + F(hi) + "]";
                return false;
            }
            value = d;
            return true;
        }

        /// <summary>A batch id: 1-64 characters of [A-Za-z0-9._-] (it goes into key=value log lines); a number is accepted.</summary>
        public static bool TryId(object v, out string id, out string error)
        {
            error = null;
            id = v is double ? ((double)v).ToString("R", CultureInfo.InvariantCulture) : v as string;
            if (string.IsNullOrEmpty(id) || id.Length > 64) { error = "id: 1-64 characters of [A-Za-z0-9._-]"; id = null; return false; }
            foreach (char c in id)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
                if (!ok) { error = "id: 1-64 characters of [A-Za-z0-9._-]"; id = null; return false; }
            }
            return true;
        }

        // ------------------------------------------------------------------------------------------ hold ladder

 /// <summary>The first rung's camera height: <c>yHint + holdAbove</c> with a hint, else <c>fromY</c>.</summary>
        public static double FirstCamY(S4Column c, S4Options o)
        {
            return c.HasHint ? c.YHint + o.HoldAbove : o.FromY;
        }

        /// <summary>The ray origin of every rung of the column: the top of the first rung's box.</summary>
        public static double OriginY(S4Column c, S4Options o)
        {
            return FirstCamY(c, o) + BoxHalf;
        }

        /// <summary>The lowest camera height of the ladder: <c>minY</c>, or <c>max(minY, yHint − hintSpan)</c> with a hint.</summary>
        public static double FloorY(S4Column c, S4Options o)
        {
            return c.HasHint ? Math.Max(o.MinY, c.YHint - o.HintSpan) : o.MinY;
        }

        /// <summary>The next rung (<paramref name="camY"/> − descend) while it stays at or above the floor; false at the last rung (and always without hold).</summary>
        public static bool TryNextCamY(S4Column c, S4Options o, double camY, out double next)
        {
            next = camY - o.Descend;
            if (!o.Hold) return false;
            return next >= FloorY(c, o) - 1e-9;
        }

        /// <summary>The number of rungs of the column's ladder (1 without hold).</summary>
        public static int RungCount(S4Column c, S4Options o)
        {
            int n = 1;
            double y = FirstCamY(c, o), next;
            while (TryNextCamY(c, o, y, out next)) { n++; y = next; if (n > 100000) break; }
            return n;
        }

        /// <summary>
 /// One frame of a hold. <paramref name="got"/>/<paramref name="hitY"/>: the layer-30 ray's hit from the column origin;
 /// <paramref name="boxBuilt"/>: the collision clipmap reports the camera box (camY ± 16 m at the column) built;
 /// <paramref name="spanBuilt"/>: it reports built over the camera box joined with the hit ±1 m (so every cell between
 /// them); <paramref name="builtFrames"/>: consecutive frames of boxBuilt. A hit is accepted once the span is built
 /// for <see cref="MinBuiltFrames"/> frames; a hit inside the box whose span never reads built is accepted unverified
 /// at the rung timeout; a hit below the box with the box built and the span not (a collider left loaded from an
 /// earlier hold, with unloaded cells above it) is not trusted: the camera descends to it. No hit: descend once the
 /// box is built (empty) or at the rung timeout; past the last rung the column fails.
 /// </summary>
        public static S4Step Decide(bool got, double hitY, double camY, bool boxBuilt, bool spanBuilt, int builtFrames, ulong rungElapsedMs, ulong timeoutMs, bool lastRung)
        {
            bool timedOut = rungElapsedMs >= timeoutMs;
            bool settled = boxBuilt && builtFrames >= MinBuiltFrames;
            if (got && !double.IsNaN(hitY))
            {
                if (spanBuilt && builtFrames >= MinBuiltFrames) return S4Step.Accept;
                bool inBox = hitY >= camY - BoxHalf - BoxSlack;
                if (inBox) return timedOut ? S4Step.AcceptUnverified : S4Step.Wait;
                if (settled || timedOut) return lastRung ? S4Step.AcceptUnverified : S4Step.NextRung;
                return S4Step.Wait;
            }
            if (settled || timedOut) return lastRung ? S4Step.Fail : S4Step.NextRung;
            return S4Step.Wait;
        }

        /// <summary>The vertical extent [lo, hi] the span check asks of the clipmap: the camera box joined with the hit ±1 m.</summary>
        public static void SpanY(double hitY, double camY, out double lo, out double hi)
        {
            lo = Math.Min(hitY - 1.0, camY - BoxHalf);
            hi = Math.Max(hitY + 1.0, camY + BoxHalf);
        }

        // ------------------------------------------------------------------------------------------ render pass

 /// <summary>
 /// A vertical line at (px, pz) against triangle abc (world metres): true with the crossing height <paramref name="y"/>
 /// when (px, pz) lies inside the triangle's XZ projection (edges included); false for a triangle seen edge-on from
 /// above. <paramref name="up"/>: Cross(b − a, c − a).y &gt; 0, the front face looking up under Unity's clockwise
 /// winding (HYPOTHESIS for Subnautica's terrain meshes: the counts are reported, the choice does not depend on it).
 /// </summary>
        public static bool VerticalHit(double px, double pz, double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz,
                                       out double y, out bool up)
        {
            y = double.NaN;
            double ny = (bz - az) * (cx - ax) - (bx - ax) * (cz - az);
            up = ny > 0;
            double det = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
            double scale = Math.Abs(bx - ax) + Math.Abs(bz - az) + Math.Abs(cx - ax) + Math.Abs(cz - az);
            if (Math.Abs(det) <= 1e-12 * (scale * scale + 1e-30)) return false;
            double l1 = ((bz - cz) * (px - cx) + (cx - bx) * (pz - cz)) / det;
            double l2 = ((cz - az) * (px - cx) + (ax - cx) * (pz - cz)) / det;
            double l3 = 1.0 - l1 - l2;
            const double eps = -1e-9;
            if (l1 < eps || l2 < eps || l3 < eps) return false;
            y = l1 * ay + l2 * by + l3 * cy;
            return true;
        }

        // ------------------------------------------------------------------------------------------ log line and rows

 /// <summary>
 /// "DE.S4 PROBE i=… x=… z=… hit=… y=… collider=… layer=… camY=… waitMs=… renderY=… delta=…" (the S4 contract's
 /// order) followed by batch, yHint, rungs, verified, originY, renderFirstY, renderHits, renderVia, renderVisible,
 /// state, note, error. key=value tokens without spaces (names and notes have spaces replaced); numbers invariant,
 /// "nan" when absent.
 /// </summary>
        public static string ProbeLine(string batch, S4ColumnResult r)
        {
            var sb = new StringBuilder(320);
            sb.Append("DE.S4 PROBE i=").Append(r.I.ToString(CultureInfo.InvariantCulture))
              .Append(" x=").Append(F(r.X)).Append(" z=").Append(F(r.Z)).Append(" hit=").Append(B(r.Hit))
              .Append(" y=").Append(F(r.Y)).Append(" collider=").Append(Token(r.Collider)).Append(" layer=").Append(r.Layer.ToString(CultureInfo.InvariantCulture))
              .Append(" camY=").Append(F(r.CamY)).Append(" waitMs=").Append(r.WaitMs.ToString(CultureInfo.InvariantCulture))
              .Append(" renderY=").Append(F(r.RenderY)).Append(" delta=").Append(F4(r.RenderDelta))
              .Append(" batch=").Append(Token(batch)).Append(" yHint=").Append(F(r.YHint)).Append(" rungs=").Append(r.Rungs.ToString(CultureInfo.InvariantCulture))
              .Append(" verified=").Append(B(r.Verified)).Append(" originY=").Append(F(r.OriginY)).Append(" renderFirstY=").Append(F(r.RenderFirstY))
              .Append(" renderHits=").Append(r.RenderHits.ToString(CultureInfo.InvariantCulture)).Append(" renderVia=").Append(Token(r.RenderVia))
              .Append(" renderVisible=").Append(B(r.RenderVisible)).Append(" state=").Append(Token(r.State))
              .Append(" note=").Append(Token(r.RenderNote)).Append(" error=").Append(Token(r.Error));
            return sb.ToString();
        }

        /// <summary>The structured JSONL row "s4.probe": {"t", "tickMs", "frame", "level", "event", "ev", "data": {batch, …the result}}.</summary>
        public static string ProbeRow(string batch, S4ColumnResult r, string utc, ulong tickMs, ulong frame)
        {
            var j = new JsonOut();
            RowHead(j, "s4.probe", utc, tickMs, frame);
            j.Obj("data").Str("batch", batch);
            r.WriteJson(j);
            j.EndObj();
            return j.ToString();
        }

        internal static void RowHead(JsonOut j, string ev, string utc, ulong tickMs, ulong frame)
        {
            j.Str("t", utc).Num("tickMs", tickMs).Num("frame", frame).Str("level", "info").Str("event", ev).Str("ev", ev);
        }

        /// <summary>The descent line of a hold that found nothing in its box (or a hit it does not trust below it).</summary>
        public static string DescendLine(string batch, S4Column c, int rung, double fromCamY, double toCamY, string why)
        {
            return "DE.S4 DESCEND batch=" + Token(batch) + " i=" + c.I.ToString(CultureInfo.InvariantCulture) + " x=" + F(c.X) + " z=" + F(c.Z) +
                   " rung=" + rung.ToString(CultureInfo.InvariantCulture) + " camY=" + F(fromCamY) + " next=" + F(toCamY) + " why=" + Token(why);
        }

        /// <summary>"JSON" of the column in progress for the status reply (null when none).</summary>
        public static string CurrentJson(S4Column c, int rung, double camY, double originY, ulong elapsedMs, int builtFrames)
        {
            if (c == null) return "null";
            var j = new JsonOut();
            j.Num("i", (long)c.I).Num("x", c.X).Num("z", c.Z).Num("yHint", c.YHint).Str("state", "holding").Num("rung", (long)rung)
             .Num("camY", camY).Num("originY", originY).Num("elapsedMs", (long)elapsedMs).Num("builtFrames", (long)builtFrames);
            return j.ToString();
        }

        /// <summary>The idle status reply (no batch since the bridge started).</summary>
        public static void WriteIdle(JsonObjectWriter w)
        {
            w.Bool("ok", true).Str("cmd", Cmd).Scalar("batch", null).Str("state", "idle").Num("done", 0L).Num("columns", 0L).Num("from", 0L)
             .Num("pending", 0L).Raw("results", "[]").Num("returned", 0L).Num("next", 0L).Bool("more", false).Bool("truncated", false).Bool("all", false)
             .Raw("current", "null");
        }

        public static string F(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? "nan" : v.ToString("0.###", CultureInfo.InvariantCulture); }
        public static string F4(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? "nan" : v.ToString("0.####", CultureInfo.InvariantCulture); }
        public static string B(bool b) { return b ? "true" : "false"; }

        /// <summary>A value safe for a key=value token: "none" when empty, whitespace and '=' replaced by '_'.</summary>
        public static string Token(string s)
        {
            if (string.IsNullOrEmpty(s)) return "none";
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s) sb.Append(char.IsWhiteSpace(ch) || ch == '=' ? '_' : ch);
            return sb.ToString();
        }
    }
}
