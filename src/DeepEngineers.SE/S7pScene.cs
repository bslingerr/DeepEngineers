using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using VRage.Game.Entity;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S7p-scene-above, SE side (ROADMAP "### S7p Polish pass" P1, P6b, P7; CONTRACT §5.4 "S7p scene-above"; rules in Wire
 /// <see cref="S7pScene"/>): the settings and the <c>de.s7p</c> verb, the <c>s7p.scene</c> row (1 Hz). The work lives in
 /// <c>GridMirrorProducer.S7p.cs</c> (missiles, projections), <see cref="SubpartSpin"/> (render-thread spinners),
 /// <see cref="ToolSubparts"/> (the held tool's own subparts), <see cref="S7pProjection"/> (admission of a projector's
 /// blueprint) and <see cref="S7pScenePatches"/> (hidden projected blocks re-read at once).
 /// <para>P1 finding: the scene skip clears <c>m_drawScene</c>, so
 /// <c>FullDraw</c> never calls <c>DrawScene</c> and clears the backbuffer to (0,0,0,0);
 /// every transparent pass (GPU particles and billboards in <c>MyManagers.RenderScheduler</c>, then
 /// <c>MyBillboardRenderer.RenderLDR</c> / <c>RenderPostPP</c>) runs inside <c>DrawGameScene</c> against
 /// <c>MyGBuffer.Main</c> (depth, HDR LBuffer, tone mapping) of the same frame. Nothing of SE's particle pass survives the
 /// skip and no hook can run it without the rest of SE's scene: route A is impossible, route B (the S7 event stream played
 /// by Subnautica above water) is the rule
 /// Subnautica's own fires, e.g. the Aurora's, now hidden by the landmark rule).</para>
 /// Update thread.
 /// </summary>
    internal static class S7pSceneSe
    {
        public const string Cmd = S7pScene.Cmd;
        private const ulong RowEveryMs = 1000;

        private static bool _init;
        private static S7pAboveMode _above;
        private static bool _projections = true, _subparts = true;
        private static ulong _nextRowMs;

        // counters (the s7p.scene row)
        public static long MissileEnters, MissileLeaves, MissileCapDrops, MissilesSeen;
        public static int MissilesMirrored, MissilesInSphere;
        public static long ProjectionEnters, ProjectionLeaves;
        public static int ProjectionsMirrored;
        public static readonly long[] ProjectionVerdicts = new long[16];
        /// </summary>
        public static long PastePreviewTicks;
        public static long HiddenParts, HiddenBlocks, ProjectionMarks;
        public static long SpinRows, SpinCapDrops, SpinFieldMissing, SpinnersMarked;
        public static long ToolPartsSent, ToolPartRows;
        public static long SolidFiltered;
        public static string PatchState = "not applied";

        public static void EnsureInit()
        {
            if (_init) return;
            _init = true;
            string a = Env(S7pScene.AboveEnvVar), p = Env(S7pScene.ProjectionsEnvVar), s = Env(S7pScene.SubpartsEnvVar);
            bool fromEnv;
            _above = S7pScene.AboveFromEnv(a, out fromEnv);
            _projections = S7pScene.OnOffFromEnv(p, true);
            _subparts = S7pScene.OnOffFromEnv(s, true);
            DeLog.Info("DE.S7p scene-above SE: above=" + S7pScene.Name(_above) + " (" + S7pScene.AboveEnvVar + " " + (a ?? "unset: default events") + ") projections=" +
                       (_projections ? "on" : "off") + " (" + S7pScene.ProjectionsEnvVar + " " + (p ?? "unset") + ") subparts=" + (_subparts ? "on" : "off") +
                       " (" + S7pScene.SubpartsEnvVar + " " + (s ?? "unset") + "); route B: SE's particle pass does not survive the scene skip (MyRender11.FullDraw), " +
                       "Subnautica plays the above-water variants; de.s7p to switch");
        }

        private static string Env(string name)
        {
            try { return Environment.GetEnvironmentVariable(name); } catch (Exception) { return null; }
        }

        public static S7pAboveMode Above { get { EnsureInit(); return _above; } }
        public static bool ProjectionsOn { get { EnsureInit(); return _projections; } }
        public static bool SubpartsOn { get { EnsureInit(); return _subparts; } }

        // ------------------------------------------------------------------------------------------ de.s7p

 /// <summary>
 /// <c>de.s7p {above: events|se|off, projections: on|off, subparts: on|off}</c> (or <c>{argv: [...]}</c>; <c>{action:
 /// scene}</c> or no key: status). The verb is shared (<see cref="S7pScene.IsMine"/>): null for another feature's keys.
 /// From chat the route is relayed to Subnautica (its <c>de.s7p</c> holds its own route copy).
 /// </summary>
        public static Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            if (cmd != Cmd) return null;
            EnsureInit();
            // the verb is shared by the S7p features (CONTRACT §5.4): another feature's keys go on to its own handler
            object v;
            List<string> words = null;
            if (args != null && args.TryGetValue("argv", out v) && v is System.Collections.IList)
            {
                words = new List<string>();
                foreach (object o in (System.Collections.IList)v) words.Add(o != null ? Convert.ToString(o, CultureInfo.InvariantCulture) : "");
            }
            string action = args != null && args.TryGetValue("action", out v) ? v as string : null;
            var keys = new List<string>();
            if (args != null) foreach (string k in args.Keys) if (k != "argv" && k != "action") keys.Add(k);
            if (!S7pScene.IsMine(keys, action, words)) return null;
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                var kv = new Dictionary<string, string>(StringComparer.Ordinal);
                string bad = null;
                if (words != null) kv = S7pScene.ParseArgv(words, out bad);
                if (args != null)
                    foreach (string k in new[] { "above", "projections", "subparts" })
                        if (args.TryGetValue(k, out v) && v != null) kv[k] = v is bool ? ((bool)v ? "on" : "off") : Convert.ToString(v, CultureInfo.InvariantCulture);
                string error = bad != null ? bad + " (" + S7pScene.Usage + ")" : null;
                string s;
                if (error == null && kv.TryGetValue("above", out s))
                {
                    S7pAboveMode m;
                    if (!S7pScene.TryParseAbove(s, out m)) error = "above: events | se | off";
                    else if (m != _above) { _above = m; DeLog.Info("DE.S7p above=" + S7pScene.Name(m) + " (de.s7p via " + via + ")"); }
                }
                bool on;
                if (error == null && kv.TryGetValue("projections", out s))
                {
                    if (!S7pScene.TryParseOnOff(s, out on)) error = "projections: on | off";
                    else if (on != _projections) { _projections = on; DeLog.Info("DE.S7p projections=" + (on ? "on" : "off") + " (de.s7p via " + via + ")"); }
                }
                if (error == null && kv.TryGetValue("subparts", out s))
                {
                    if (!S7pScene.TryParseOnOff(s, out on)) error = "subparts: on | off";
                    else if (on != _subparts) { _subparts = on; DeLog.Info("DE.S7p subparts=" + (on ? "on" : "off") + " (de.s7p via " + via + ")"); }
                }
                // the chat path relays the route to Subnautica (the harness sends de.s7p to each side itself)
                string relayed = null;
                if (error == null && via == "chat" && kv.ContainsKey("above"))
                {
                    var relay = S3Commands.RelayToSn;
                    var ra = new Dictionary<string, object>(StringComparer.Ordinal) { { "above", S7pScene.Name(_above) } };
                    relayed = relay != null ? (relay(Cmd, ra, null) ?? "relayed") : "no relay";
                }
                d["ok"] = error == null;
                if (error != null) d["error"] = error;
                if (relayed != null) d["relayToSn"] = relayed;
                Status(d);
                Row("command");
            }
            catch (Exception e)
            {
                d["ok"] = false;
                d["error"] = "de.s7p failed: " + e.GetType().Name + ": " + e.Message;
            }
            return d;
        }

        private static void Status(Dictionary<string, object> d)
        {
            d["above"] = S7pScene.Name(_above);
            d["projections"] = _projections;
            d["subparts"] = _subparts;
            d["missilesMirrored"] = MissilesMirrored;
            d["missilesInSphere"] = MissilesInSphere;
            d["projectionsMirrored"] = ProjectionsMirrored;
            d["spinners"] = SubpartSpin.Tracked;
            d["toolParts"] = ToolSubparts.Count;
            d["patch"] = PatchState;
            d["line"] = "DE.S7p SE above=" + S7pScene.Name(_above) + " projections=" + (_projections ? "on" : "off") + " (" + ProjectionsMirrored + " mirrored) subparts=" +
                        (_subparts ? "on" : "off") + " (" + SubpartSpin.Tracked + " spinners, " + ToolSubparts.Count + " tool parts) missiles " + MissilesMirrored + "/" + MissilesInSphere;
        }

        // ------------------------------------------------------------------------------------------ rows

 /// <summary>The 1 Hz row (called by the producer's S7p tick).</summary>
        public static void RowIfDue(ulong nowMs)
        {
            if (nowMs < _nextRowMs) return;
            _nextRowMs = nowMs + RowEveryMs;
            Row("tick");
        }

        /// <summary>
 /// s7p.scene: the route and toggles; missiles (seen in the sphere, mirrored, enters/leaves, over the cap);
 /// projections (mirrored, enters/leaves, verdicts by reason, hidden cube parts and blocks skipped, transparency
 /// marks); spinners (tracked, rows spun, over the cap, missing speed fields, subparts marked Dynamic); the held
 /// tool's subparts (sent, rows); solid-list filtering (skins and rooms never see a projection or a missile).
 /// </summary>
        public static void Row(string why)
        {
            var sb = new StringBuilder(768);
            sb.Append("{\"t\":"); JsonText.AppendString(sb, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            sb.Append(",\"ev\":\"s7p.scene\"");
            S5Rows.Str(sb, "why", why);
            S5Rows.Str(sb, "above", S7pScene.Name(_above));
            S5Rows.Bool(sb, "projections", _projections);
            S5Rows.Bool(sb, "subparts", _subparts);
            S5Rows.Int(sb, "missilesInSphere", MissilesInSphere);
            S5Rows.Int(sb, "missilesMirrored", MissilesMirrored);
            S5Rows.Int(sb, "missileEnters", MissileEnters);
            S5Rows.Int(sb, "missileLeaves", MissileLeaves);
            S5Rows.Int(sb, "missileCapDrops", MissileCapDrops);
            S5Rows.Int(sb, "projectionsMirrored", ProjectionsMirrored);
            S5Rows.Int(sb, "projectionEnters", ProjectionEnters);
            S5Rows.Int(sb, "projectionLeaves", ProjectionLeaves);
            sb.Append(",\"projectionVerdicts\":{");
            bool first = true;
            for (int i = 0; i < ProjectionVerdicts.Length; i++)
            {
                if (ProjectionVerdicts[i] == 0) continue;
                if (!first) sb.Append(',');
                first = false;
                JsonText.AppendString(sb, S7pProjectionRules.Name((S7pProjectionVerdict)i));
                sb.Append(':').Append(ProjectionVerdicts[i].ToString(CultureInfo.InvariantCulture));
            }
            sb.Append('}');
            S5Rows.Int(sb, "hiddenParts", HiddenParts);
            S5Rows.Int(sb, "hiddenBlocks", HiddenBlocks);
            S5Rows.Int(sb, "transparencyMarks", ProjectionMarks);
            S5Rows.Int(sb, "spinners", SubpartSpin.Tracked);
            S5Rows.Int(sb, "spinnersMarked", SpinnersMarked);
            S5Rows.Int(sb, "spinRows", SpinRows);
            S5Rows.Int(sb, "spinCapDrops", SpinCapDrops);
            S5Rows.Int(sb, "spinFieldMissing", SpinFieldMissing);
            S5Rows.Int(sb, "toolParts", ToolSubparts.Count);
            S5Rows.Int(sb, "toolPartsSent", ToolPartsSent);
            S5Rows.Int(sb, "toolPartRows", ToolPartRows);
            S5Rows.Int(sb, "solidFiltered", SolidFiltered);
            S5Rows.Str(sb, "patch", PatchState);
            sb.Append('}');
            Jsonl.WriteLineBuffered(sb.ToString());
        }
    }

    // ===================================================================================================== P7 admission

 /// <summary>
 /// P7: may a preview grid be mirrored as a projection? (<see cref="EntitySet.Mirrorable"/> asks for every
 /// <c>IsPreview</c> grid.) Members VERIFIED <c>MyCubeGrid.Projector</c>
 /// <c>MyProjectorBase.ProjectedGrid</c> (public, <c>PreviewGrids[0]</c>), <c>MyProjectorBase.Scale</c> (public float,
 /// ), <c>Sandbox.ModAPI.Ingame.IMyProjector.IsProjecting</c> (explicit, → <c>m_clipboard.IsActive</c>),
 /// <c>MyCubeGrid.BlocksCount</c> (public int). The cube build pass own preview has no projector: never mirrored
 /// here (the S7a placement ghost carries it).
 /// </summary>
    internal static class S7pProjection
    {
        public static bool Admissible(MyCubeGrid g, out string why)
        {
            why = null;
            S7pProjectionVerdict v;
            try { v = Verdict(g); }
            catch (Exception e) { why = "projection check failed: " + e.GetType().Name; return false; }
            if (v == S7pProjectionVerdict.Admit) return true;
            int i = (int)v;
            // the cube build pass own preview (no projector) is asked every tick: not counted
            if (v != S7pProjectionVerdict.NoProjector && v != S7pProjectionVerdict.NotPreview && i < S7pSceneSe.ProjectionVerdicts.Length) S7pSceneSe.ProjectionVerdicts[i]++;
            why = v == S7pProjectionVerdict.NoProjector ? "preview (projection or build preview)" : "projection not mirrored: " + S7pProjectionRules.Name(v);
            return false;
        }

        public static S7pProjectionVerdict Verdict(MyCubeGrid g)
        {
            MyProjectorBase p = g != null ? g.Projector : null;
            bool hasProjector = p != null && !p.Closed && !p.MarkedForClose;
            bool projecting = hasProjector && ((Sandbox.ModAPI.Ingame.IMyProjector)p).IsProjecting;
            bool isProjected = hasProjector && ReferenceEquals(p.ProjectedGrid, g);
            float scale = hasProjector ? p.Scale : 1f;
            bool already = g != null && GridMirrorProducer.IsProjectionMirrored(g.EntityId);

            bool pasting = !hasProjector && g != null && g.IsPreview && ClipboardHolds(g);
            if (pasting) S7pSceneSe.PastePreviewTicks++;
            if (pasting && !S10Se.PastePreviewAllowed()) return S7pProjectionVerdict.TooManyBlocks; // S10 hook (part 3): the clipboard's blocks against the paste preview cap
            return S7pProjectionRules.Admit(S7pSceneSe.ProjectionsOn, g != null && g.IsPreview, hasProjector, projecting, isProjected, scale,
                                             g != null ? g.BlocksCount : 0, GridMirrorProducer.ProjectionCount, already, pasting);
        }

        /// <summary>
 /// The grid is one of the clipboard's preview grids while a paste is active
 ///.
 /// </summary>
        private static bool ClipboardHolds(MyCubeGrid g)
        {
            try
            {
                Sandbox.Game.SessionComponents.Clipboard.MyClipboardComponent c = Sandbox.Game.SessionComponents.Clipboard.MyClipboardComponent.Static;
                MyGridClipboard cb = c != null ? c.Clipboard : null;
                return cb != null && cb.IsActive && cb.PreviewGrids != null && cb.PreviewGrids.Contains(g);
            }
            catch (Exception) { return false; }
        }
    }

    // ===================================================================================================== P6b spinners

 /// <summary>
 /// P6b: block subparts SE spins in the render thread only (<see cref="S7pSpinRules.SpinTable"/>; their entity local
 /// matrix never moves, so the S5 motion detector cannot see them). <see cref="Spins"/> marks such a subpart Dynamic when
 /// the exporter reads its block; <see cref="Apply"/> integrates its spin from the render component's private speed field
 /// (one cached compiled getter per type; reflection as the fallback; a missing field logs once and leaves that type
 /// static) and composes it into the subpart's C2 row. At most <see cref="S7pScene.MaxSpinners"/> tracked (over: static,
 /// counted); entries unseen for 600 builds are dropped. Update thread (the PrepareForDraw postfix).
 /// </summary>
    internal static class SubpartSpin
    {
        private struct Key : IEquatable<Key>
        {
            public readonly long Grid;
            public readonly ulong Part;
            public Key(long g, ulong p) { Grid = g; Part = p; }
            public bool Equals(Key o) { return Grid == o.Grid && Part == o.Part; }
            public override bool Equals(object obj) { return obj is Key && Equals((Key)obj); }
            public override int GetHashCode() { return (Grid.GetHashCode() * 397) ^ Part.GetHashCode(); }
        }

        private sealed class State
        {
            public double Angle, Omega, Nominal;
            public long Build;
        }

        private sealed class Kind
        {
            public S7pSpinner Def;
            public bool Known;
            /// <summary>Getter for the subpart render (null when <see cref="S7pSpinner.OnBlockRender"/>).</summary>
            public Func<object, float> Get;
        }

        private static readonly Dictionary<Type, Kind> _kinds = new Dictionary<Type, Kind>();
        private static readonly Dictionary<Type, Func<object, float>> _blockGetters = new Dictionary<Type, Func<object, float>>();
        private static readonly Dictionary<Key, State> _state = new Dictionary<Key, State>();
        private static readonly List<Key> _gone = new List<Key>();
        private static long _builds;
        private static long _lastQpc;
        private static double _dt;

        public static int Tracked { get { return _state.Count; } }

        /// <summary>The subpart is a render-thread spinner (its render type in the table, the speed field present) and subparts are on.</summary>
        public static bool Spins(MyEntitySubpart sub, MyCubeBlock block)
        {
            if (sub == null || !S7pSceneSe.SubpartsOn) return false;
            try
            {
                Kind k = KindOf(sub.Render != null ? sub.Render.GetType() : null);
                if (k == null || !k.Known) return false;
                if (k.Def.OnBlockRender) return block != null && block.Render != null && BlockGetter(block.Render.GetType(), k.Def.SpeedField) != null;
                return k.Get != null;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Once per C2 build: the real time since the previous build (clamped to [0, 0.1] s) and the stale-entry sweep.</summary>
        public static void BeginBuild()
        {
            long now = Stopwatch.GetTimestamp();
            _dt = _lastQpc == 0 ? 0 : Math.Min(0.1, Math.Max(0, (now - _lastQpc) / (double)Stopwatch.Frequency));
            _lastQpc = now;
            _builds++;
            if (_builds % 600 != 0 || _state.Count == 0) return;
            _gone.Clear();
            foreach (KeyValuePair<Key, State> kv in _state) if (_builds - kv.Value.Build > 600) _gone.Add(kv.Key);
            for (int i = 0; i < _gone.Count; i++) _state.Remove(_gone[i]);
        }

        /// <summary>Composes the integrated spin into a dynamic subpart's grid-local matrix; false (unchanged) for a non-spinner.</summary>
        public static bool Apply(long gridId, SubRef r, ref SeMatrix composed)
        {
            if (!S7pSceneSe.SubpartsOn || r == null || r.Sub == null) return false;
            Kind k = KindOf(r.Sub.Render != null ? r.Sub.Render.GetType() : null);
            if (k == null || !k.Known) return false;
            Func<object, float> get = k.Def.OnBlockRender
                ? (r.Block != null && r.Block.Render != null ? BlockGetter(r.Block.Render.GetType(), k.Def.SpeedField) : null)
                : k.Get;
            if (get == null) return false;
            object target = k.Def.OnBlockRender ? (object)r.Block.Render : r.Sub.Render;
            float speed;
            try { speed = get(target); }
            catch (Exception) { return false; }
            var key = new Key(gridId, r.Key);
            State s;
            if (!_state.TryGetValue(key, out s))
            {
                if (_state.Count >= S7pScene.MaxSpinners) { S7pSceneSe.SpinCapDrops++; return false; }
                s = new State();
                _state[key] = s;
            }
            s.Build = _builds;
            S7pSpinRules.Step(ref s.Angle, ref s.Omega, ref s.Nominal, speed, k.Def.Up, k.Def.Down, _dt);
            if (s.Angle == 0 && s.Omega == 0) return false;
            composed = S7pSpinRules.Spun(composed, k.Def.Axis, s.Angle);
            S7pSceneSe.SpinRows++;
            return true;
        }

        public static void Forget() { _state.Clear(); }

        private static Kind KindOf(Type t)
        {
            if (t == null) return null;
            Kind k;
            if (_kinds.TryGetValue(t, out k)) return k;
            k = new Kind();
            S7pSpinner def;
            if (S7pSpinRules.TryFind(t.FullName, out def))
            {
                k.Def = def;
                k.Known = true;
                if (!def.OnBlockRender)
                {
                    k.Get = Getter(t, def.SpeedField);
                    if (k.Get == null)
                    {
                        k.Known = false;
                        S7pSceneSe.SpinFieldMissing++;
                        DeLog.Info("DE.S7p spin MISSING " + t.FullName + "." + def.SpeedField + " (that subpart type stays static)");
                    }
                }
            }
            _kinds[t] = k;
            return k;
        }

        private static Func<object, float> BlockGetter(Type t, string field)
        {
            Func<object, float> g;
            if (_blockGetters.TryGetValue(t, out g)) return g;
            g = Getter(t, field);
            if (g == null)
            {
                S7pSceneSe.SpinFieldMissing++;
                DeLog.Info("DE.S7p spin MISSING " + t.FullName + "." + field + " (that block's spinning subpart stays static)");
            }
            _blockGetters[t] = g;
            return g;
        }

        /// <summary>The private instance float <paramref name="field"/> of <paramref name="t"/> or a base type (generic bases included).</summary>
        private static Func<object, float> Getter(Type t, string field)
        {
            FieldInfo f = null;
            for (Type x = t; x != null && f == null; x = x.BaseType)
                f = x.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (f == null || f.FieldType != typeof(float)) return null;
            try
            {
                ParameterExpression p = Expression.Parameter(typeof(object), "o");
                Func<object, float> c = Expression.Lambda<Func<object, float>>(Expression.Field(Expression.Convert(p, f.DeclaringType), f), p).Compile();
                return o => c(o);
            }
            catch (Exception)
            {
                return o => (float)f.GetValue(o);
            }
        }
    }

    // ===================================================================================================== P6b held tool subparts

 /// <summary>
 /// P6b: the held tool's own subparts
 /// S5 mirrors only the tool's main model (CharacterExport, key 3), so the bit was missing. Each subpart (nested, at most
 /// <see cref="S7pScene.MaxToolSubparts"/>, with a model) goes as one more character part, class Tool, Dynamic, key
 /// <see cref="S7pScene.ToolSubpartKey"/>, posed by its own C2 row (its local chain × the tool relative to the character).
 /// Driven by <c>CharacterExport.TickTool</c> (hooks) and <see cref="SubgridPoses"/>. Update thread.
 /// </summary>
    internal static class ToolSubparts
    {
        private sealed class Sent
        {
            public MyEntitySubpart Sub;
            public ulong Key;
        }

        private static readonly List<Sent> _sent = new List<Sent>();
        private static long _charId;
        private static MyEntity _tool;

        public static int Count { get { return _sent.Count; } }

        /// <summary>The tool changed (CharacterExport): the parts of the old tool are removed when they were sent with it.</summary>
        public static void OnToolChange(long characterId, bool toolWasSent)
        {
            if (toolWasSent && _charId == characterId)
                foreach (Sent s in _sent) GridMirrorProducer.EnqueueControl(S3Codec.PartRemoveMsg(characterId, s.Key));
            _sent.Clear();
            _tool = null;
            _charId = 0;
        }

        /// <summary>The new tool was sent (CharacterExport): its subparts as character parts (models requested on C3).</summary>
        public static void Send(ulong nowMs, MyCharacter c, MyEntity tool)
        {
            _sent.Clear();
            _tool = null;
            _charId = 0;
            if (c == null || tool == null || !S7pSceneSe.SubpartsOn) return;
            _tool = tool;
            _charId = c.EntityId;
            Collect(tool, 0);
            for (int i = 0; i < _sent.Count; i++)
            {
                MyEntitySubpart sub = _sent[i].Sub;
                string asset = sub.Model.AssetName;
                float scale = sub.Model.ScaleFactor;
                var p = new PartSnapshot
                {
                    PartKey = _sent[i].Key,
                    ModelKey = S3Keys.ModelKey(asset, 0, scale),
                    AssetName = asset,
                    ModelScale = scale,
                    Local = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 },
                    Flags = S5Conv.Part(S5Conv.PartClass.Tool, S5Conv.PartFlags.Dynamic),
                };
                GridMirrorProducer.EnsureModelFor(nowMs, p);
                GridMirrorProducer.EnqueueControl(S3Codec.PartAddMsg(c.EntityId, p));
                S7pSceneSe.ToolPartsSent++;
            }
            if (_sent.Count > 0) DeLog.Info("DE.S7p tool " + tool.GetType().Name + ": " + _sent.Count + " subpart(s) mirrored with it (" + string.Join(", ", _sent.Select(s => s.Sub.Model.AssetName).ToArray()) + ")");
        }

        private static void Collect(MyEntity e, int depth)
        {
            if (e == null || e.Subparts == null || depth > 3) return;
            foreach (KeyValuePair<string, MyEntitySubpart> kv in e.Subparts)
            {
                if (_sent.Count >= S7pScene.MaxToolSubparts) return;
                MyEntitySubpart sub = kv.Value;
                if (sub == null || sub.Closed) continue;
                if (sub.Model != null && !string.IsNullOrEmpty(sub.Model.AssetName))
                    _sent.Add(new Sent { Sub = sub, Key = S7pScene.ToolSubpartKey(_sent.Count) });
                Collect(sub, depth + 1);
            }
        }

        /// </summary>
        public static void AppendRows(List<C2RowCandidate> into, MyCharacter c)
        {
            if (_sent.Count == 0 || c == null || c.EntityId != _charId || _tool == null || _tool.Closed) return;
            SeMatrix toolRel = SubgridPoses.Relative(_tool, c);
            for (int i = 0; i < _sent.Count; i++)
            {
                SeMatrix chain;
                if (!Chain(_sent[i].Sub, _tool, out chain)) continue;
                into.Add(new C2RowCandidate { Kind = C2RowKind.Tool, Distance = 0, Row = InstanceMath.MakeSubpartRow((ulong)c.EntityId, _sent[i].Key, InstanceMath.Multiply(chain, toolRel)) });
                S7pSceneSe.ToolPartRows++;
            }
        }

        /// <summary>The subpart's matrix in its tool's frame: its local, then each parent subpart's local (child × parent).</summary>
        private static bool Chain(MyEntitySubpart sub, MyEntity tool, out SeMatrix m)
        {
            m = InstanceMath.Identity;
            if (sub == null || sub.Closed) return false;
            Matrix l = sub.PositionComp.LocalMatrixRef;
            m = GridExporter.Se(ref l);
            MyEntity p = sub.Parent;
            int guard = 0;
            while (p != null && !ReferenceEquals(p, tool) && guard++ < 6)
            {
                Matrix pl = p.PositionComp.LocalMatrixRef;
                m = InstanceMath.Multiply(m, GridExporter.Se(ref pl));
                p = p.Parent;
            }
            return ReferenceEquals(p, tool);
        }
    }

    // ===================================================================================================== P7 patch

 /// <summary>
 /// P7: <c>Sandbox.Game.Entities.Blocks.MyProjectorBase.SetTransparency(MySlimBlock cubeBlock, float transparency)</c>
 /// postfix:
 /// every block the projector shows or hides is re-read by the S5 exporter at once (its fat part and subparts; the render
 /// cell hook already re-reads the cube parts), so a block welded in SE leaves Subnautica's hologram on the next export.
 /// A missing target logs once and leaves the slow sweep to catch the change. Same Harmony id as every SE patch.
 /// </summary>
    internal static class S7pScenePatches
    {
        public const string Target = "MyProjectorBase.SetTransparency(MySlimBlock, float)";
        private static bool _applied;

        public static void Apply(Harmony harmony)
        {
            if (_applied || harmony == null) return;
            _applied = true;
            try
            {
                MethodInfo m = AccessTools.Method(typeof(MyProjectorBase), "SetTransparency", new[] { typeof(MySlimBlock), typeof(float) });
                if (m == null || m.IsStatic || m.ReturnType != typeof(void))
                {
                    S7pSceneSe.PatchState = "missing";
                    DeLog.Info("DE.Harmony MISSING " + Target + " (S7p projections: hidden blocks wait for the sweep)");
                    return;
                }
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(S7pScenePatches).GetMethod(nameof(SetTransparencyPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                S7pSceneSe.PatchState = "ok";
                DeLog.Info("DE.Harmony S7p OK " + Target + " postfix (projected blocks re-read when SE shows or hides them)");
            }
            catch (Exception e)
            {
                S7pSceneSe.PatchState = "failed";
                DeLog.Info("DE.Harmony MISSING " + Target + " patch failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        // never throws into SE's projector update
        private static void SetTransparencyPostfix(MySlimBlock cubeBlock)
        {
            try
            {
                if (cubeBlock == null || cubeBlock.CubeGrid == null || !GridMirrorProducer.IsProjectionMirrored(cubeBlock.CubeGrid.EntityId)) return;
                GridMirrorProducer.MarkBlockFromHook(cubeBlock);
                S7pSceneSe.ProjectionMarks++;
            }
            catch (Exception) { }
        }
    }
}
