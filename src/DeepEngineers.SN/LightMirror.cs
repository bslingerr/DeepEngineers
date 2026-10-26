using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S7 lights, the Subnautica pool (CONTRACT §5.4 "C2 lights (S7; S7Lights)"; ROADMAP S7.N3): the newest C2 light rows
 /// (Wire <see cref="S7LightReader"/>, registered on the consumer's C2 reader <c>Extensions</c>, copied in the same
 /// seqlock window as the entities) drawn every frame by a pool of at most 64 Unity <c>Light</c>s:
 /// <list type="bullet">
 /// <item>Placement: an attached row (<c>ownerId</c> = a mirrored grid, subgrid or the mirrored character) is a child of

 /// interpolated hull; a world row (<c>ownerId</c> 0) sits at <c>pos</c> (SN world) under the pool root. A row whose
 /// </item>
 /// <item>Look: Point or Spot (<c>spotAngle</c> = the full cone), <c>color</c> = the row's gamma RGB, <c>range</c> =
 /// range × <see cref="RangeScale"/> × the kind's range scale, <c>intensity</c> = intensity × <see cref="IntensityScale"/> ×
 /// the kind's intensity scale (S7p-character P10: <see cref="KindIntensity"/>, the suit lamp 1/25; global tunables default 1.0:
 /// HYPOTHESIS until S7.N3 compares an SE Interior Light with the Seamoth cabin light
 /// shadows on the 2 nearest spot rows with Shadows (Wire <see cref="LightPoolRules.ShadowPlan"/>; S7.M18 = no cap), every
 /// other light none; falloff is logged only (Unity has no exponent). Drawn above and under water alike.</item>
 /// <item>The calibration source: <c>de.lights {action: seamoth}</c> lists the spawned Seamoths' <c>Light</c>s
 /// <c>{action: calibrate, se, seamoth[, apply]}</c> turns two probe luminances into the ratio and
 /// the suggested scale (Wire <see cref="LightCalibration"/>), <c>{scale, range}</c> set the tunables live
 /// (environment <c>DEEPENGINEERS_LIGHT_INTENSITY</c> / <c>DEEPENGINEERS_LIGHT_RANGE</c> at start).</item>
 /// </list>
 /// Rows <c>s7.lights</c> at 1 Hz. ≤ 0.2 ms per frame; no allocation per frame after the pool is built. Main thread.
 /// </summary>
    internal sealed class LightMirror
    {
        public const string IntensityVar = "DEEPENGINEERS_LIGHT_INTENSITY", RangeVar = "DEEPENGINEERS_LIGHT_RANGE";
        public const ulong RowMs = 1000;
        public const int Max = S7Conventions.LightLimits.MaxLights;

        public static float IntensityScale = 1f, RangeScale = 1f;

        /// <summary>
 /// S7p-character (P10, "the light of my suit is absolutely blinding specially at night"): the per-kind scales on top of
 /// the global ones (<see cref="S7pLightScale"/>: block point, block spot, character point, character spot; the kind
 /// from the row's own flags). Defaults <see cref="S7pLightScale.DefaultIntensity"/>, environment
 /// <c>DEEPENGINEERS_SN_LIGHT_SCALE</c> at start, live <c>de.s7 {lightScale}</c> (chat: <c>/de sn s7 lightScale suit 0.04</c>).
 /// </summary>
        public static readonly float[] KindIntensity = (float[])S7pLightScale.DefaultIntensity.Clone();
        public static readonly float[] KindRange = (float[])S7pLightScale.DefaultRange.Clone();
        private static string _kindNote = "defaults";
        private readonly long[] _appliedByKind = new long[S7pLightScale.Kinds];
        private readonly int[] _lastByKind = new int[S7pLightScale.Kinds];
        /// <summary>The Unity intensity and range of the nearest character spot row drawn last frame (−1 = none): the suit lamp as Subnautica draws it.</summary>
        public float LastSuitIntensity = -1f, LastSuitRange = -1f;

        private readonly MirrorConsumer _m;
        public readonly S7LightReader Reader = new S7LightReader();
        private readonly SeLight[] _rows = new SeLight[Max];
        private readonly uint[] _flags = new uint[Max];
        private readonly bool[] _shadows = new bool[Max];
        private readonly Light[] _pool = new Light[Max];
        private readonly Transform[] _parent = new Transform[Max];
        private GameObject _root;
        private bool _registered, _disposed;
        private ulong _nextRowMs, _lastTick;
        private uint _lastFound;

        public long Frames, Created, Recreated, MissingOwner, Applied;
        /// <summary>Review F20: frames whose newest light snapshot was older than 1 s (the pool off).</summary>
        public long StaleFrames;
        public int LastRows, LastApplied, LastShadowed, PoolSize;
        /// <summary>Review F9: spot rows with Shadows in the last snapshot (before the shadow cap).</summary>
        public int LastShadowCandidates;

        public LightMirror(MirrorConsumer m)
        {
            _m = m;
        }

        public void Start()
        {
            ReadTunables();
            if (_m != null && _m.C2 != null && !_m.C2.Extensions.Contains(Reader))
            {
                _m.C2.Extensions.Add(Reader);
                _registered = true;
            }
            DeLog.Info("DE.S7 lights pool: " + Max + " Unity lights at most, " + S7Conventions.LightLimits.MaxShadowedSpots + " shadowed spots, intensityScale=" +
                       IntensityScale.ToString("0.###", CultureInfo.InvariantCulture) + " rangeScale=" + RangeScale.ToString("0.###", CultureInfo.InvariantCulture) +
                       " (" + IntensityVar + ", " + RangeVar + ", de.lights); kinds " + S7pLightScale.Describe(KindIntensity, KindRange) + " (" + _kindNote + "; " +
                       S7pLightScale.EnvVar + ", de.s7 lightScale); reader " + (_registered ? "on the C2 reader" : "NOT registered (no C2 reader)"));
        }

        private static void ReadTunables()
        {
            float v;
            if (TryEnv(IntensityVar, out v)) IntensityScale = v;
            if (TryEnv(RangeVar, out v)) RangeScale = v;
            // S7p-character (P10): the per-kind scales from DEEPENGINEERS_SN_LIGHT_SCALE (kind=intensity[:range],…)
            S7pLightScale.Reset(KindIntensity, KindRange);
            string s = null;
            try { s = Environment.GetEnvironmentVariable(S7pLightScale.EnvVar); } catch (Exception) { }
            string error;
            if (string.IsNullOrEmpty(s)) _kindNote = "defaults";
            else if (S7pLightScale.TryParseList(s, KindIntensity, KindRange, out error)) _kindNote = S7pLightScale.EnvVar + "=" + s;
            else
            {
                _kindNote = S7pLightScale.EnvVar + " partly applied: " + error;
                DeLog.Warn("DE.S7p lights " + S7pLightScale.EnvVar + "='" + s + "' not understood: " + error + " (the entries before it are applied)");
            }
        }

        /// <summary>S7p-character (P10): the S7a <c>de.s7</c> dispatcher's hook: <c>{lightScale: …}</c> or the chat relay's argv starting with lightScale.</summary>
        public static bool HandlesKindCommand(Dictionary<string, object> a)
        {
            if (a == null) return false;
            if (a.ContainsKey("lightScale")) return true;
            List<string> argv = ArgvOf(a);
            return argv != null && argv.Count > 0 && string.Equals(argv[0], "lightScale", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> ArgvOf(Dictionary<string, object> a)
        {
            object v;
            if (!a.TryGetValue("argv", out v) || v == null) return null;
            var list = v as System.Collections.IList;
            if (list == null) return null;
            var r = new List<string>(list.Count);
            foreach (object o in list) r.Add(o == null ? "" : Convert.ToString(o, CultureInfo.InvariantCulture));
            return r;
        }

        /// <summary>
 /// <c>de.s7 {lightScale: "status" | "reset" | "&lt;kind&gt; &lt;factor&gt; [range]"}</c>, <c>{lightScale: {kind, factor[, range]}}</c>
 /// or argv <c>[lightScale, kind, factor, range?]</c> (the chat relay <c>/de sn s7 lightScale suit 0.04</c>).
 /// </summary>
        public static void KindCommand(Dictionary<string, object> a, JsonObjectWriter reply)
        {
            var tokens = new List<string>();
            object v;
            List<string> argv = ArgvOf(a);
            if (a.TryGetValue("lightScale", out v) && v != null)
            {
                var d = v as Dictionary<string, object>;
                if (d != null)
                {
                    tokens.Add(S3Args.Str(d, "kind", ""));
                    object f;
                    if (d.TryGetValue("factor", out f) && f != null) tokens.Add(Convert.ToString(f, CultureInfo.InvariantCulture));
                    if (d.TryGetValue("range", out f) && f != null) tokens.Add(Convert.ToString(f, CultureInfo.InvariantCulture));
                }
                else foreach (string t in Convert.ToString(v, CultureInfo.InvariantCulture).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)) tokens.Add(t);
            }
            else if (argv != null) for (int i = 1; i < argv.Count; i++) tokens.Add(argv[i]);
            string action, error;
            LightKind kind;
            double factor, range;
            if (!S7pLightScale.TryParseCommand(tokens, out action, out kind, out factor, out range, out error))
            {
                reply.Bool("ok", false).Str("error", error).Str("lightScale", S7pLightScale.Describe(KindIntensity, KindRange));
                return;
            }
            if (action == "reset")
            {
                S7pLightScale.Reset(KindIntensity, KindRange);
                _kindNote = "reset (de.s7)";
                DeLog.Warn("DE.S7p lights kinds reset to the defaults " + S7pLightScale.Describe(KindIntensity, KindRange) + " (de.s7 lightScale)");
            }
            else if (action == "set")
            {
                KindIntensity[(int)kind] = (float)factor;
                if (!double.IsNaN(range)) KindRange[(int)kind] = (float)range;
                _kindNote = "live (de.s7)";
                DeLog.Warn("DE.S7p lights " + S7pLightScale.Name(kind) + " intensity×" + factor.ToString("0.####", CultureInfo.InvariantCulture) +
                           (double.IsNaN(range) ? "" : " range×" + range.ToString("0.####", CultureInfo.InvariantCulture)) + " -> " + S7pLightScale.Describe(KindIntensity, KindRange) + " (de.s7 lightScale)");
            }
            reply.Bool("ok", true).Str("lightScale", S7pLightScale.Describe(KindIntensity, KindRange)).Str("note", _kindNote)
                 .Num("globalIntensity", IntensityScale).Num("globalRange", RangeScale)
                 .Str("line", "DE.S7p lights " + S7pLightScale.Describe(KindIntensity, KindRange) + " global " + IntensityScale.ToString("0.###", CultureInfo.InvariantCulture) +
                              " (SE suit lamp 25 × characterSpot = Unity " + (25f * IntensityScale * KindIntensity[(int)LightKind.CharacterSpot]).ToString("0.###", CultureInfo.InvariantCulture) + ")");
        }

        private static bool TryEnv(string name, out float v)
        {
            v = 0f;
            string s = null;
            try { s = Environment.GetEnvironmentVariable(name); } catch (Exception) { }
            double d;
            if (string.IsNullOrEmpty(s) || !double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) || !(d > 0) || d > 100) return false;
            v = (float)d;
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_registered && _m != null && _m.C2 != null) _m.C2.Extensions.Remove(Reader);
            for (int i = 0; i < Max; i++)
            {
                try { if (_pool[i] != null) UnityEngine.Object.Destroy(_pool[i].gameObject); } catch (Exception) { }
                _pool[i] = null;
                _parent[i] = null;
            }
            try { if (_root != null) UnityEngine.Object.Destroy(_root); } catch (Exception) { }
            _root = null;
        }

        public void OnNewSePeer()
        {
            Reader.Reset();
            for (int i = 0; i < Max; i++) if (_pool[i] != null) _pool[i].enabled = false;
        }

        // ============================================================================================ per frame

        public void Tick(ulong now)
        {
            if (_disposed) return;
            Frames++;
            ulong tick;
            uint found;
            int serial;
            int n = Reader.Snapshot(_rows, out tick, out found, out serial);
            // review F20: a snapshot older than 1 s (SE exited to the menu or stalled) is not drawn: the pool goes dark
            bool stale = n > 0 && Reader.SecondsSinceCommit > S7LightReader.StaleSeconds;
            if (stale) { n = 0; StaleFrames++; }
            _lastTick = tick;
            _lastFound = found;
            int candidates = 0;
            for (int i = 0; i < n; i++)
            {
                _flags[i] = _rows[i].flags;
                // review F9: spot rows asking for shadows before the cap (S7.N3.shadows is judged only with >= 3 of them)
                if ((_flags[i] & SeLightFlags.Spot) != 0 && (_flags[i] & SeLightFlags.Shadows) != 0) candidates++;
            }
            LastShadowCandidates = candidates;
            int shadowed = LightPoolRules.ShadowPlan(_flags, n, S7Mutants.M18 ? -1 : S7Conventions.LightLimits.MaxShadowedSpots, _shadows);
            int applied = 0, appliedShadowed = 0;
            Array.Clear(_lastByKind, 0, _lastByKind.Length);
            LastSuitIntensity = LastSuitRange = -1f;
            for (int i = 0; i < n; i++)
            {
                Transform parent;
                bool attached;
                if (_m.S7tIsImpostor(_rows[i].ownerId)) { S7tImpostors.LightsSkipped++; continue; } // S7t-sn-altitude (T3): an impostor has no lights
                if (!Owner(_rows[i].ownerId, out parent, out attached)) { MissingOwner++; continue; }
                Light l = Slot(applied);
                Place(l, applied, ref _rows[i], parent, attached);
                LightKind kind = Look(l, ref _rows[i], _shadows[i]);
                _lastByKind[(int)kind]++; // S7p-character (P10)
                _appliedByKind[(int)kind]++;
                if (kind == LightKind.CharacterSpot && LastSuitIntensity < 0f) { LastSuitIntensity = l.intensity; LastSuitRange = l.range; } // rows are nearest first
                if (_shadows[i]) appliedShadowed++;
                applied++;
            }
            for (int i = applied; i < Max; i++) if (_pool[i] != null && _pool[i].enabled) _pool[i].enabled = false;
            LastRows = n;
            LastApplied = applied;
            LastShadowed = appliedShadowed;
            Applied += applied;
            if (now >= _nextRowMs)
            {
                _nextRowMs = now + RowMs;
                Row("periodic");
            }
        }

        /// </summary>
        private bool Owner(ulong ownerId, out Transform parent, out bool attached)
        {
            attached = ownerId != 0;
            parent = null;
            if (!attached) { parent = Root().transform; return true; }
            MirrorGridView v;
            if (_m.ViewMap.TryGetValue(ownerId, out v) && v.Alive && v.HasPose && v.EntityT != null && v.Frame.activeInHierarchy) { parent = v.EntityT; return true; }
            CharacterMirror c = _m.Character;
            if (c != null && c.CharacterId == ownerId && c.EntityTransform != null) { parent = c.EntityTransform; return true; }
            return false;
        }

        private GameObject Root()
        {
            if (_root == null)
            {
                _root = new GameObject("DE.S7.Lights");
                _root.transform.position = Vector3.zero;
                _root.transform.rotation = Quaternion.identity;
            }
            return _root;
        }

        private Light Slot(int i)
        {
            Light l = _pool[i];
            if (l != null) return l;
            if (!ReferenceEquals(l, null)) Recreated++;
            var go = new GameObject("DE.S7.Light." + i.ToString(CultureInfo.InvariantCulture));
            go.transform.SetParent(Root().transform, false);
            l = go.AddComponent<Light>();
            l.enabled = false;
            _pool[i] = l;
            _parent[i] = Root().transform;
            Created++;
            if (i + 1 > PoolSize) PoolSize = i + 1;
            return l;
        }

        private void Place(Light l, int slot, ref SeLight r, Transform parent, bool attached)
        {
            Transform t = l.transform;
            if (!ReferenceEquals(_parent[slot], parent) || t.parent != parent)
            {
                t.SetParent(parent, false);
                _parent[slot] = parent;
            }
            bool spot = S7LightRow.IsSpot(r.flags);
            if (attached)
            {
                // 5: Mirror(v) = (x, y, -z))
                t.localPosition = new Vector3(r.pos.x, r.pos.y, -r.pos.z);
                var d = new Vector3(r.dir.x, r.dir.y, -r.dir.z);
                if (spot && d.sqrMagnitude > 1e-8f) t.localRotation = Quaternion.LookRotation(d);
            }
            else
            {
                t.position = new Vector3(r.pos.x, r.pos.y, r.pos.z);
                var d = new Vector3(r.dir.x, r.dir.y, r.dir.z);
                if (spot && d.sqrMagnitude > 1e-8f) t.rotation = Quaternion.LookRotation(d);
            }
        }

        private static LightKind Look(Light l, ref SeLight r, bool shadows)
        {
            bool spot = S7LightRow.IsSpot(r.flags);
            LightType type = spot ? LightType.Spot : LightType.Point;
            if (l.type != type) l.type = type;
            if (spot) l.spotAngle = LightPoolRules.SpotAngle(r.coneDeg);
            // S7p-character (P10): SE's value × the global tunable × the kind's scale (the suit lamp 25 → 1.0 by default)
            LightKind kind = S7pLightScale.KindOf(r.flags);
            l.range = S7pLightScale.Range(r.range, RangeScale, KindRange[(int)kind]);
            l.intensity = S7pLightScale.Intensity(kind, r.intensity, IntensityScale, KindIntensity[(int)kind]);   // capped per kind
            l.intensity *= S15SnPicks.LightFactor((int)kind); // S15 hook (pick 8): the block lights dimmed at night, the Subnautica way
            l.color = new Color(r.colour.x, r.colour.y, r.colour.z, 1f);
            LightShadows sh = shadows ? LightShadows.Soft : LightShadows.None;
            if (l.shadows != sh) l.shadows = sh;
            int cm = S7pSelfShadowRules.IsCharacterKind(kind) ? S7pSelfShadow.LampMask : -1; //: the character never casts from its own lamps
            if (l.cullingMask != cm) l.cullingMask = cm;
            S13Sn.LightLook(l, kind, S7LightRow.GetFalloff(ref r), shadows); // S13 hook (point 4): SE's near-field shadow rules and the falloff fit
            if (!l.enabled) l.enabled = true;
            return kind;
        }

        // ============================================================================================ rows and C11

        public void Row(string why)
        {
            JsonOut j = S5Rows.Begin("s7.lights");
            j.Str("why", why).Num("snFrame", (long)Time.frameCount).Num("c2Tick", _lastTick).Num("rows", (long)LastRows).Num("applied", (long)LastApplied)
             .Num("shadowed", (long)LastShadowed).Num("pool", (long)PoolSize).Num("found", (long)_lastFound).Num("missingOwner", MissingOwner)
             .Num("intensityScale", IntensityScale).Num("rangeScale", RangeScale).Bool("m18", S7Mutants.M18).Num("created", Created).Num("recreated", Recreated)
             .Num("readerCommits", Reader.Commits).Num("rowsNotV1", Reader.RowsNotV1).Num("rowsInvalid", Reader.RowsInvalid).Bool("registered", _registered)
             .Num("shadowCandidates", (long)LastShadowCandidates).Num("staleFrames", StaleFrames); // review F9, F20
 // S7p-character (P10): the kind scales, the rows per kind last frame and the suit lamp as drawn (Unity intensity/range)
            j.Str("kindScales", S7pLightScale.Describe(KindIntensity, KindRange)).Str("kindNote", _kindNote)
             .Num("blockPoint", (long)_lastByKind[0]).Num("blockSpot", (long)_lastByKind[1]).Num("characterPoint", (long)_lastByKind[2]).Num("characterSpot", (long)_lastByKind[3])
             .Num("suitIntensity", LastSuitIntensity).Num("suitRange", LastSuitRange)
             .Num("charLayer", (long)S7pSelfShadow.Layer).Str("selfShadow", S7pSelfShadow.Note); //: the self-shadow rule
            S5Rows.End(j);
        }

        /// <summary><c>de.lights {action: status | seamoth | calibrate, se, seamoth, apply} | {scale, range}</c>.</summary>
        public bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            if (req.Cmd != "de.lights") return false;
            Dictionary<string, object> a = S3Args.ToDict(req.Args);
            reply.Str("cmd", req.Cmd);
            if (a.ContainsKey("scale") || a.ContainsKey("range"))
            {
                double s = S3Args.Num(a, "scale", IntensityScale), r = S3Args.Num(a, "range", RangeScale);
                if (!(s > 0) || s > 100 || !(r > 0) || r > 100) { reply.Bool("ok", false).Str("error", "scale and range in (0, 100]"); return true; }
                IntensityScale = (float)s;
                RangeScale = (float)r;
                DeLog.Warn("DE.S7 lights intensityScale=" + IntensityScale.ToString("0.###", CultureInfo.InvariantCulture) + " rangeScale=" + RangeScale.ToString("0.###", CultureInfo.InvariantCulture) + " (de.lights)");
                Row("tunables");
                Status(reply.Bool("ok", true));
                return true;
            }
            string action = S3Args.Str(a, "action", "status").Trim().ToLowerInvariant();
            switch (action)
            {
                case "status":
                    Row("status");
                    Status(reply.Bool("ok", true));
                    return true;
                case "seamoth":
                {
                    string list = SeamothLights();
                    DeLog.Info("DE.S7 lights Seamoth sources: " + list);
                    JsonOut j = S5Rows.Begin("s7.lights");
                    j.Str("why", "seamoth").Num("snFrame", (long)Time.frameCount).Str("seamoth", list).Num("intensityScale", IntensityScale).Num("rangeScale", RangeScale);
                    S5Rows.End(j);
                    Status(reply.Bool("ok", true));
                    reply.Str("seamoth", list);
                    return true;
                }
                case "calibrate":
                {
                    double se = S3Args.Num(a, "se", double.NaN), sm = S3Args.Num(a, "seamoth", double.NaN);
                    double ratio = LightCalibration.Ratio(se, sm);
                    double suggest = LightCalibration.SuggestScale(IntensityScale, se, sm);
                    bool apply = false;
                    object v;
                    if (a.TryGetValue("apply", out v)) S3Args.TryBool(v, out apply);
                    if (apply) IntensityScale = (float)suggest;
                    JsonOut j = S5Rows.Begin("s7.lights");
                    j.Str("why", "calibrate").Num("snFrame", (long)Time.frameCount).Num("seLum", se).Num("seamothLum", sm).Num("ratio", ratio)
                     .Bool("pass", LightCalibration.Pass(ratio)).Num("suggestedScale", suggest).Bool("applied", apply).Num("intensityScale", IntensityScale);
                    S5Rows.End(j);
                    Status(reply.Bool("ok", true));
                    reply.Num("ratio", ratio).Bool("pass", LightCalibration.Pass(ratio)).Num("suggestedScale", suggest).Bool("applied", apply);
                    return true;
                }
                default:
                    reply.Bool("ok", false).Str("error", "de.lights: unknown action '" + action + "' (status | seamoth | calibrate; or {scale, range})");
                    return true;
            }
        }

        public void Status(JsonObjectWriter reply)
        {
            reply.Num("rows", (long)LastRows).Num("applied", (long)LastApplied).Num("shadowed", (long)LastShadowed).Num("pool", (long)PoolSize)
                 .Num("found", (long)_lastFound).Num("c2Tick", _lastTick).Num("missingOwner", MissingOwner).Num("intensityScale", IntensityScale).Num("rangeScale", RangeScale)
                 .Bool("m18", S7Mutants.M18).Bool("registered", _registered).Num("readerCommits", Reader.Commits).Num("rowsNotV1", Reader.RowsNotV1)
                 .Str("kindScales", S7pLightScale.Describe(KindIntensity, KindRange)).Num("suitIntensity", LastSuitIntensity).Num("suitRange", LastSuitRange) // S7p-character (P10)
                 .Str("line", "DE.S7 lights rows=" + LastRows + " applied=" + LastApplied + " shadowed=" + LastShadowed + " pool=" + PoolSize + " scale=" +
                              IntensityScale.ToString("0.###", CultureInfo.InvariantCulture) + " kinds " + S7pLightScale.Describe(KindIntensity, KindRange));
        }

        /// <summary>Every spawned Seamoth's lights: name, type, intensity, range, angle, colour, head or cabin.</summary>
        private static string SeamothLights()
        {
            var sb = new StringBuilder(256);
            SeaMoth[] all = UnityEngine.Object.FindObjectsOfType<SeaMoth>();
            if (all == null || all.Length == 0) return "no Seamoth in the scene (de.spawn Seamoth 1 3)";
            foreach (SeaMoth s in all)
            {
                Transform heads = s.toggleLights != null && s.toggleLights.lightsParent != null ? s.toggleLights.lightsParent.transform : null;
                sb.Append(s.name).Append('{');
                foreach (Light l in s.GetComponentsInChildren<Light>(true))
                {
                    bool head = heads != null && l.transform.IsChildOf(heads);
                    sb.Append(head ? "head:" : "cabin:").Append(l.name).Append(' ').Append(l.type).Append(" i=").Append(l.intensity.ToString("0.###", CultureInfo.InvariantCulture))
                      .Append(" r=").Append(l.range.ToString("0.##", CultureInfo.InvariantCulture)).Append(" a=").Append(l.spotAngle.ToString("0.#", CultureInfo.InvariantCulture))
                      .Append(" c=").Append(l.color.r.ToString("0.##", CultureInfo.InvariantCulture)).Append(',').Append(l.color.g.ToString("0.##", CultureInfo.InvariantCulture))
                      .Append(',').Append(l.color.b.ToString("0.##", CultureInfo.InvariantCulture)).Append(" on=").Append(l.enabled && l.gameObject.activeInHierarchy).Append("; ");
                }
                sb.Append("} ");
            }
            return sb.ToString();
        }
    }
}
