using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using DeepEngineers.Wire;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S5 "whole grids + character" on the Subnautica side (ROADMAP S5 SN plugin; CONTRACT §5.4 "C1–C4 conventions for
 /// S5"), driven by the bridge behaviour after S3 and S5a. The mirror work itself lives in the S3 consumer
 /// (<see cref="MirrorConsumer"/> with <see cref="S5Mirror"/> and <see cref="CharacterMirror"/>); this host owns:
 /// <list type="bullet">
 /// <item>the first-person body switch <c>de.s5 fpbody=se|diver</c> (env <c>DEEPENGINEERS_SN_FPBODY</c>; S7p-character
 /// Gate</c> is ForceOff every
 /// tick (S5a keeps the animator, O₂, breath and bubbles; the diver's arms, mask and shadow never show), with
 /// <c>diver</c> the user's gate is back and the SE body is not drawn in first person (the two are never shown
 /// together); the <c>s7p.char</c> row (1 Hz) audits Subnautica's own Player renderers; <c>de.s5 paint=bake|approx</c>
 /// switches the P9 paint;</item>
 /// <item>the Subnautica S5 seeded failures S5.M1 (rows and roots one snapshot behind) and S5.M2 (per-model flip
 /// skipped), read every frame (S5.M4/M5 are applied by S2's recipe step);</item>
 /// <item>the vanilla glass material (read once per world from the Seamoth prefab's renderers; HYPOTHESIS);</item>
 /// <item>the dev command <c>de.s5</c>: status, fpbody, sub (s5.sub rows for n s), coh (s5.coh rows for an entity
 /// point), retarget on|off, rig (the rig line again), placed (s5.placed rows again).</item>
 /// </list>
 /// Each step runs in its own guard (<c>DE.S5 FEATURE … DISABLED</c> once); S0–S5a never depend on S5.
 /// </summary>
    internal sealed class S5Host : IDisposable
    {
        private readonly MonoBehaviour _host;
        private readonly S3Host _s3;
        private readonly S5aHost _s5a;
        private readonly HashSet<string> _disabled = new HashSet<string>(StringComparer.Ordinal);
        private FpBody _fp;
        private string _fpNote;
        private bool _gateForced;
        private ArmsGate _gateBefore;
        private bool _glassRequested, _started;
        private long _gateForcedCount;

        public S5Host(MonoBehaviour host, S3Host s3, S5aHost s5a)
        {
            if (s3 == null) throw new ArgumentNullException("s3");
            _host = host;
            _s3 = s3;
            _s5a = s5a;
        }

        private MirrorConsumer Mirror { get { return _s3.Mirror; } }

        private Action _stepMutants, _stepPerf;
        private readonly FrameStats _perfFrames = new FrameStats();
        private ulong _now, _nextPerfMs;

        /// <summary>
 /// s5.perf (S5.T-P1, every second in every state): frames, p50/p99/max frame time, the applied frame-rate pair, the
 /// main camera's state and the S5.M4/M5 mutants (the recipe itself is S2's DE.Perf line and C7).
 /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void PerfRow()
        {
            double p50, p99, max;
            int frames, over;
            _perfFrames.Take(out p50, out p99, out max, out frames, out over);
            Camera cam = MainCamera.camera;
            JsonOut j = S5Rows.Begin("s5.perf");
            j.Num("frames", (long)frames).Num("framesOver16_7", (long)over).Num("frameP50Ms", p50).Num("frameP99Ms", p99).Num("frameMaxMs", max).Num("vSyncCount", (long)QualitySettings.vSyncCount)
             .Num("targetFrameRate", (long)Application.targetFrameRate).Bool("mainCameraEnabled", cam != null && cam.enabled).Bool("worldUp", Mirror.WorldReady)
             .Bool("s5M4", DevMutants.IsOn(DevMutants.S5FullInSpace)).Bool("s5M5", DevMutants.IsOn(DevMutants.S5CamerasOnInTrack))
             .Str("character", CharSourceRules.Name(Mirror.Character.Source)).Bool("armsGateForced", _gateForced);
            S5Rows.End(j);
        }

        private void StepMutants() { Mirror.SetS5Mutants(DevMutants.IsOn(DevMutants.S5PreviousSnapshot), DevMutants.IsOn(DevMutants.S5FlipOff)); }

        public void Start()
        {
            // verdict "SE's identity
 // everywhere"): fpbody defaults to se; S5a's diver arms are the swim-stroke option behind fpbody=diver
            _fp = S5Switches.FpBodyFromEnvironment(out _fpNote); // S7pFirstPerson.FromEnvironmentValue
            Mirror.Character.Fp = _fp;
            Mirror.Character.OnFirstPersonBody = OnFirstPersonBody;
            S7pCharacterPaint.ReadMode(); // S7p-character (P9): DEEPENGINEERS_SN_CHAR_PAINT bake|approx
            Guard("gate", KeepGate);
            string skErr;
            CanonSkeleton sk = CanonSkeleton.Embedded(out skErr);
            ulong conv = LayoutHash.Fnv1a64Ascii(S5Conv.CanonicalText());
            DeLog.Info("DE.S5 start: conventions 'ext s5 mirror 1' hash " + DeLog.Hex(conv) + (conv == S5Conv.ConventionsHash ? " (pinned)" : " MISMATCH (pinned " + DeLog.Hex(S5Conv.ConventionsHash) + ")") +
                       " skeleton canon1 " + (sk != null ? sk.Count + " bones hash " + DeLog.Hex(sk.Hash) : "UNAVAILABLE (" + skErr + ")") +
                       " fpbody=" + S5Switches.Name(_fp) + " (" + _fpNote + ") s5a=" + (_s5a != null ? "present (arms gate " + S5aRules.GateName(_s5a.Gate) + ")" : "absent") +
                       " paint=" + (S7pCharacterPaint.BakeOn ? "bake" : "approx") + " (" + S7pCharacterPaint.ModeNote + ")" +
                       " cull=" + S5Mirror.CullDistanceM.ToString("0", CultureInfo.InvariantCulture) + " m (water visibility fallback)");
            _started = true;
        }

        // ------------------------------------------------------------------------------------------ S7p-character (P8)

        private long _gateReasserted, _snLeakSeconds;
        private ulong _nextS7pRowMs;
        private int _lastSnRenderersOn, _lastSnShadowCasters;
        private bool _lastLeak;

        /// <summary>
 /// S7p-character (P8): S5a's gate follows fpbody, every tick: ForceOff under fpbody=se once SE's astronaut is known
 /// (a CharacterAdd record or a built rig), whatever the SE body's state in the frame (the diver's arms, mask and
 /// shadow never show; the first-person body is SE's astronaut or nothing); the user's gate back under fpbody=diver,
 /// and while SE has sent no character at all (S5 off on SE: the S5a sessions). A gate changed meanwhile (de.arms) is
 /// kept as the user's gate and forced off again (<see cref="S7pFirstPerson.GateFor"/>).
 /// </summary>
        private void KeepGate()
        {
            if (_s5a == null) return;
            if (!S7pFirstPerson.DiverArmsAllowed(_fp, SeCharacterKnown))
            {
                if (_s5a.Gate == ArmsGate.ForceOff) { if (!_gateForced) { _gateBefore = ArmsGate.Full; _gateForced = true; } return; }
                ArmsGate user = _s5a.Gate;
                bool again = _gateForced;
                _gateBefore = user;
                _s5a.Gate = S7pFirstPerson.GateFor(_fp, user, true);
                _gateForced = true;
                _gateForcedCount++;
                if (again) _gateReasserted++;
                DeLog.Info("DE.S5 fpbody=se: S5a arms gate " + S5aRules.GateName(user) + " -> ForceOff" + (again ? " again (changed meanwhile; kept as the diver gate)" : "") +
                           " (S7p: SE's astronaut is the first-person body; S5a keeps the animator, O2, breath and bubbles)");
            }
            else if (_gateForced) RestoreGate(_fp == FpBody.Diver ? "fpbody=diver" : "no SE character (S5 off on SE): the diver arms are the only body");
        }

        /// <summary>
 /// S7p-character (P8): SE has sent its astronaut (a CharacterAdd record) or its rig is built, sticky for this process
 /// once seen (a respawn's EntityRemove → CharacterAdd gap never brings the diver back).
 /// </summary>
        private bool SeCharacterKnown
        {
            get
            {
                if (!_seCharacterSeen && (Mirror.Character.Built || Mirror.State.Characters.Count > 0))
                {
                    _seCharacterSeen = true;
                    DeLog.Info("DE.S7p char: SE's astronaut seen; under fpbody=" + S5Switches.Name(_fp) + " the diver arms are " + (_fp == FpBody.Se ? "off from now on" : "the first-person body"));
                }
                return _seCharacterSeen;
            }
        }

        private bool _seCharacterSeen;

        /// <summary>
 /// S7p-character (P8): the <c>s7p.char</c> row (1 Hz): fpbody, the gate, the camera mode, the first-person body and the
 /// head shadow, the diver arms, Subnautica's own Player renderers still enabled (and casting), the leak verdict
 /// (<see cref="S7pFirstPerson.SnBodyLeak"/>), the paint state. CONTRACT §5.4 "S7p character rows".
 /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void S7pRow()
        {
            CharacterMirror ch = Mirror.Character;
            int on = 0, casting = 0;
            Player p = Player.main;
            if (p != null)
            {
                foreach (Renderer r in p.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                    on++;
                    if (r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off) casting++;
                }
            }
            _lastSnRenderersOn = on;
            _lastSnShadowCasters = casting;
            bool first = ch.LastCameraMode == CameraMode.FirstPerson;
            bool diver = _s5a != null && _s5a.ArmsVisibleNow;
            bool known = SeCharacterKnown;
            bool leak = S7pFirstPerson.SnBodyLeak(_fp, known, first, diver, on);
            if (leak) _snLeakSeconds++;
            if (leak != _lastLeak)
            {
                _lastLeak = leak;
                if (leak) DeLog.Warn("DE.S7p char LEAK: fpbody=se in first person but Subnautica draws its own player (diverArms=" + diver + " renderersOn=" + on + " casting=" + casting + ")");
            }
            JsonOut j = S5Rows.Begin("s7p.char");
            j.Str("fpbody", S5Switches.Name(_fp)).Str("armsGate", _s5a != null ? S5aRules.GateName(_s5a.Gate) : "no S5a").Bool("gateForced", _gateForced)
             .Num("gateReasserted", _gateReasserted).Num("cameraMode", (long)ch.LastCameraMode).Bool("firstPerson", first).Bool("characterBuilt", ch.Built).Bool("seCharacterKnown", known)
             .Bool("seBodyFirstPerson", ch.FirstPersonBodyDrawn).Bool("headShadowOnly", ch.HeadShadowOnly).Bool("diverArmsShown", diver)
             .Num("snPlayerRenderersOn", (long)on).Num("snShadowCastersOn", (long)casting).Bool("snBodyLeak", leak).Num("snLeakSeconds", _snLeakSeconds)
             .Str("paint", ch.Paint.Describe()).Num("paintBaked", (long)ch.Paint.Baked).Num("paintSlots", (long)ch.Paint.AppliedSlots)
             .Str("lightKinds", S7pLightScale.Describe(LightMirror.KindIntensity, LightMirror.KindRange));
            S5Rows.End(j);
        }

        public void Tick(ulong now)
        {
            if (!_started) return;
            _now = now;
            _perfFrames.Add(Time.unscaledDeltaTime * 1000.0);
            if (now >= _nextPerfMs)
            {
                _nextPerfMs = now + 1000;
                Guard("perf", _stepPerf ?? (_stepPerf = PerfRow));
            }
            Guard("mutants", _stepMutants ?? (_stepMutants = StepMutants));
            if (!_glassRequested && Mirror.WorldReady && _host != null) Guard("glass", RequestGlass);
            if (!Mirror.WorldReady && _glassRequested) { _glassRequested = false; Mirror.S5.GlassSource = null; } // read again in the next world
 // S7p-character (P8): the gate follows fpbody (was: restored whenever the SE body was not drawn, so the diver's arms
 // and shadow came back in every frame the SE body was missing)
            Guard("gate", _stepGate ?? (_stepGate = KeepGate));
            if (now >= _nextS7pRowMs)
            {
                _nextS7pRowMs = now + 1000;
                Guard("s7prow", _stepS7pRow ?? (_stepS7pRow = S7pRow));
            }
            S13Sn.Tick(now, Mirror); // S13 hook: the s13.char / s13.light / s13.ghost rows at 1 Hz (guarded inside)
        }

        private Action _stepGate, _stepS7pRow;

        /// <summary>
 /// The character's first-person body changed (in the camera callback, before S5a's own step of the frame): S5a's
 /// diver arms are forced off while the SE body is drawn, restored right after.
 /// </summary>
        private void OnFirstPersonBody(bool drawn)
        {
            if (_s5a == null) return;
            try
            {
                // S7p-character (P8): the gate no longer follows the SE body's state (KeepGate follows fpbody); under fpbody=se
 // the gate is forced off here at once too, in the camera callback before S5a's step of the frame
                if (drawn) _seBodyShows++;
                if (_fp == FpBody.Se && _s5a.Gate != ArmsGate.ForceOff) KeepGate(); // (drawn implies SE's astronaut is known)
            }
            catch (Exception e)
            {
                DeLog.WarnOnce("s5.gate", "DE.S5 arms gate not switched: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private long _seBodyShows;

        private void RestoreGate(string why)
        {
            if (!_gateForced || _s5a == null) return;
            _gateForced = false;
            if (_s5a.Gate == ArmsGate.ForceOff) _s5a.Gate = _gateBefore; // a dev change made meanwhile (de.arms) is kept
            DeLog.Info("DE.S5 S5a arms gate restored to " + S5aRules.GateName(_s5a.Gate) + " (" + why + ")");
        }

        private void SetFp(FpBody fp, string why)
        {
            _fp = fp;
            Mirror.Character.Fp = fp;
            if (fp == FpBody.Diver) RestoreGate("fpbody=diver");
            else Guard("gate", KeepGate); // S7p-character (P8): forced off at once
            DeLog.Warn("DE.S5 fpbody=" + S5Switches.Name(fp) + " (" + why + ")");
        }

        // ------------------------------------------------------------------------------------------ glass

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RequestGlass()
        {
            _glassRequested = true;
            _host.StartCoroutine(FetchGlass());
        }

        private IEnumerator FetchGlass()
        {
            CoroutineTask<GameObject> task = CraftData.GetPrefabForTechTypeAsync(TechType.Seamoth, false);
            yield return task;
            Guard("glass", () => TakeGlass(task));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void TakeGlass(CoroutineTask<GameObject> task)
        {
            GameObject prefab = task.GetResult();
            if (prefab == null) { Mirror.S5.GlassSourceName = "Seamoth prefab not loaded"; DeLog.Warn("DE.S5 glass: no Seamoth prefab (glass drawn with the alpha-clip style)"); return; }
            foreach (Renderer r in prefab.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                foreach (Material m in r.sharedMaterials)
                {
                    if (m == null || m.name.IndexOf("glass", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Mirror.S5.GlassSource = m;
                    Mirror.S5.GlassSourceName = m.name;
                    DeLog.Info("DE.S5 glass material source '" + m.name + "' shader=" + (m.shader != null ? m.shader.name : "null") + " (from the Seamoth prefab renderer '" + r.name +
                               "'; cloned per glass MaterialDef; HYPOTHESIS for the look)");
                    return;
                }
            }
            Mirror.S5.GlassSourceName = "no glass material on the Seamoth prefab";
            DeLog.Warn("DE.S5 glass: no material named *glass* on the Seamoth prefab (glass drawn with the alpha-clip style)");
        }

        // ------------------------------------------------------------------------------------------ commands

        public bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            if (S13Sn.Handle(req, reply, now)) return true; // S13 hook: de.s13 {status | ghost | lightFit | blockFit | nearPlane | sounds}
            if (req.Cmd != "de.s5") return false;
            Dictionary<string, object> a = S3Args.ToDict(req.Args);
            ArgvToKeys(a); // S7p-character: the chat relay (/de sn s5 fpbody se | paint approx) sends argv
            string action = S3Args.Str(a, "action", null);
            object v;
            string error = null;
            if (a.TryGetValue("fpbody", out v) || action == "fpbody")
            {
                string s = v as string ?? S3Args.Str(a, "value", null);
                FpBody fp;
                if (!S5Switches.TryParseFpBody(s, out fp)) error = "fpbody: se|diver";
                else SetFp(fp, "de.s5");
            }
            if (error == null && a.TryGetValue("paint", out v))
            {
                // S7p-character (P9): bake = SE's paint per texel on the character, approx = the S3 tint (compare)
                bool bake;
                if (!S7pPaint.TryParseMode(v as string, out bake)) error = "paint: bake|approx";
                else Mirror.Character.Paint.SetMode(bake, "de.s5");
            }
            if (error == null && (action == "sub" || a.ContainsKey("sub")))
            {
                double secs = S3Args.Num(a, "seconds", 10);
                Mirror.S5.StartSub(secs);
                DeLog.Info("DE.S5 s5.sub rows for " + secs.ToString("0.##", CultureInfo.InvariantCulture) + " s (subgrids and dynamic subparts, alpha and SE-local rows)");
            }
            if (error == null && (action == "coh" || a.ContainsKey("coh")))
            {
                ulong entity;
                double x, y, z;
                if (!TryEntity(a, out entity)) error = "coh: entity (hex 0x… or decimal) required";
                else if (!a.TryGetValue("local", out v) || !S3Args.TryVec(v, out x, out y, out z)) error = "coh: local [x, y, z] (SE-local metres) required";
                else
                {
                    double secs = S3Args.Num(a, "seconds", 10);
                    Mirror.S5.StartCoh(entity, new Vec3(x, y, z), secs);
                    DeLog.Info("DE.S5 s5.coh rows for " + DeLog.Hex(entity) + " point (" + x + ", " + y + ", " + z + ") for " + secs.ToString("0.##", CultureInfo.InvariantCulture) + " s");
                }
            }
            if (error == null && (action == "placed" || a.ContainsKey("placed")))
            {
                // s5.placed rows now: every drawn grid's sample, or the given parts of one entity (SE's s5.part keys)
                ulong entity = 0;
                if (a.ContainsKey("entity") && !TryEntity(a, out entity)) error = "placed: entity (hex 0x… or decimal)";
                List<ulong> keys = null;
                object pv;
                if (error == null && a.TryGetValue("parts", out pv) && pv != null)
                {
                    keys = new List<ulong>();
                    var list = pv as System.Collections.IList;
                    string text = pv as string;
                    var items = new List<object>();
                    if (list != null) foreach (object o in list) items.Add(o);
                    else if (text != null) foreach (string t in text.Split(',')) items.Add(t);
                    foreach (object o in items)
                    {
                        ulong k;
                        if (!TryKey(o, out k)) { error = "placed: parts must be part keys (hex 0x… or decimal)"; break; }
                        keys.Add(k);
                    }
                }
                if (error == null)
                {
                    int n = Mirror.S5.LogPlaced(entity, keys);
                    reply.Num("placedRows", (long)n);
                }
            }
            if (error == null && a.TryGetValue("retarget", out v))
            {
                bool on;
                if (!S3Args.TryBool(v, out on)) error = "retarget: true|false";
                else { Mirror.Character.SetRetarget(on); DeLog.Warn("DE.S5 retarget " + (on ? "on" : "OFF") + " (de.s5)"); }
            }
            reply.Bool("ok", error == null).Str("cmd", req.Cmd);
            if (error != null) reply.Str("error", error);
            reply.Str("fpbody", S5Switches.Name(_fp)).Str("fpbodyNote", _fpNote).Num("gateReasserted", _gateReasserted).Num("seBodyShows", _seBodyShows) // S7p-character
                 .Num("snPlayerRenderersOn", (long)_lastSnRenderersOn).Num("snShadowCastersOn", (long)_lastSnShadowCasters).Num("snLeakSeconds", _snLeakSeconds)
                 .Str("paint", Mirror.Character.Paint.Describe()).Str("paintLast", Mirror.Character.Paint.LastLine)
                 .Bool("armsGateForced", _gateForced).Num("armsGateForcedCount", _gateForcedCount)
                 .Str("armsGate", _s5a != null ? S5aRules.GateName(_s5a.Gate) : "no S5a").Bool("subActive", Mirror.S5.SubActive).Bool("cohActive", Mirror.S5.CohActive)
                 .Str("glass", Mirror.S5.GlassSourceName ?? (Mirror.S5.GlassSource != null ? Mirror.S5.GlassSource.name : "not read"))
                 .Str("s5Disabled", _disabled.Count == 0 ? "none" : string.Join(",", new List<string>(_disabled).ToArray()));
            Mirror.Status(reply);
            return true;
        }

        /// <summary>
 /// S7p-character: SE's chat relay (<c>/de sn s5 fpbody se</c>, <c>/de sn s5 paint approx</c>) sends
 /// <c>{argv: [...]}</c>; "key value" pairs become keys (an existing key wins), a lone word becomes the action.
 /// </summary>
        private static void ArgvToKeys(Dictionary<string, object> a)
        {
            object v;
            if (a == null || !a.TryGetValue("argv", out v)) return;
            var list = v as System.Collections.IList;
            if (list == null || list.Count == 0) return;
            for (int i = 0; i < list.Count; i++)
            {
                string k = list[i] == null ? "" : Convert.ToString(list[i], CultureInfo.InvariantCulture).Trim();
                if (k.Length == 0) continue;
                int eq = k.IndexOf('=');
                if (eq > 0) { string key = k.Substring(0, eq); if (!a.ContainsKey(key)) a[key] = k.Substring(eq + 1); continue; }
                if (i + 1 < list.Count && (k == "fpbody" || k == "paint" || k == "retarget"))
                {
                    if (!a.ContainsKey(k)) a[k] = Convert.ToString(list[i + 1], CultureInfo.InvariantCulture);
                    i++;
                    continue;
                }
                if (!a.ContainsKey("action")) a["action"] = k;
            }
        }

        private static bool TryEntity(Dictionary<string, object> a, out ulong id)
        {
            id = 0;
            object v;
            if (!a.TryGetValue("entity", out v) || v == null) return false;
            return TryKey(v, out id);
        }

        private static bool TryKey(object v, out ulong id)
        {
            id = 0;
            if (v == null) return false;
            string s = v as string;
            if (s != null)
            {
                s = s.Trim();
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return ulong.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
                return ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
            }
            double d;
            if (!S1Args.TryDouble(v, out d) || d < 0) return false;
            id = (ulong)d;
            return true;
        }

        public void Dispose()
        {
            try { RestoreGate("S5 host disposed"); } catch (Exception) { }
            try { Mirror.Character.OnFirstPersonBody = null; } catch (Exception) { }
            _started = false;
        }

        private void Guard(string feature, Action a)
        {
            if (_disabled.Contains(feature)) return;
            try { a(); }
            catch (Exception e)
            {
                _disabled.Add(feature);
                DeLog.Error("DE.S5 FEATURE " + feature + " DISABLED after " + e.GetType().Name + ": " + e.Message + " (the rest of the bridge keeps running)\n" + e);
            }
        }
    }
}
