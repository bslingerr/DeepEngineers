using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;
using FMOD.Studio;
using FMODUnity;
using UnityEngine;
using UWE;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S15 "Gameplay and immersion polish", the Subnautica side (ROADMAP §14 "### S15"; docs/S15-BRIEF.md parts A and C; CONTRACT
 /// §5.4 "S15 conventions"). Main thread, once per frame from Bridge.Update (S15 hook), only in the world with SE alive.
 /// <list type="bullet">
 /// SE decides every event (the S15 gate over S16's voice
 /// rule) and counts it in the C5 block (format c5.s15, version 2); Subnautica plays one line per increment
 /// (<see cref="S15SnVoice"/>), one at a time, through S16's voice mix: the Cyclops's own <c>SubRoot.welcomeNotification</c>
 /// <c>enginePowerUpNotification</c>, <c>enginePowerDownNotification</c> of the Cyclops prefab
 /// and for a small grid the Seamoth's own boarding line: <c>Vehicle.welcomeNotification</c>
 /// of the Seamoth prefab
 /// else its <c>Vehicle.enterSound</c> (played
 /// on the hand click), else <c>SeaMoth.enterSeamoth</c>'s asset
 /// Each <c>VoiceNotification.sound</c> plays as an FMOD instance the way
 /// <c>FMODUWE.PlayOneShot</c> does at the
 /// camera at <c>S16VoiceMix.VoiceVolume</c>, and is followed to its end
 /// Nothing starts while the S2 rule says SE's audio is live (C5 <c>snBusTarget</c> under 0.5; S15.M7 ignores it). Each
 /// resolved FMOD path is logged once; each line is one log line and an <c>s15.voice</c> row.</item>
 /// <item><b>Subnautica's audio across the surface</b> (format audio.groups): <see cref="S15SnMix"/> (AudioMix's S15 hook).</item>
 /// <item><b>Subnautica's story off</b> (formats story.off, aurora.off): <see cref="S15SnStory"/>.</item>
 /// <item><b>C, SE's planets in Subnautica's sky</b>: <see cref="S15SnSky"/>.</item>
 /// <item><b>The kept picks</b>: the night lights, the lens splash, the sonar, the seabase and the fish (<see cref="S15SnPicks"/>).

 /// (LowOxygenAlert.cs) is never touched; one <c>s15.alert</c> row per world says it is there and enabled.</item>
 /// </list>
 /// Off with <c>DEEPENGINEERS_S15=off</c> (Subnautica's env); dev command <c>de.s15</c>. Never throws.
 /// </summary>
    internal static unsafe class S15Sn
    {
        public const string Verb = "de.s15";
        public const string EnvVar = "DEEPENGINEERS_S15";
        private static bool _configured;
        public static bool Enabled { get; private set; } = true;
        public static bool VoiceOn = true;

        private static readonly S15SnVoice Voice = new S15SnVoice();
        private static readonly S15EventsState Events = new S15EventsState();
        private static readonly FMODAsset[] _lines = new FMODAsset[S15Conventions.Event.Count + 1];
        private static readonly string[] _lineSource = new string[S15Conventions.Event.Count + 1];
        private static readonly List<KeyValuePair<EventInstance, S15SnLineWatch>> Playing = new List<KeyValuePair<EventInstance, S15SnLineWatch>>();
        private static bool _resolved, _loggedSources, _seamothLoading, _seamothDone, _alertRow;
        private static float _nextResolveS, _giveUpS = -1f, _nextAlertS;
        private static bool _inWorldLast;
        private static long _s16VoicesSeen = -1;
        public static long Played, Suppressed, Silent, Errors, Ended, EndedComplete;
        private static float _nextErrorS;

        public static void LoadConfig()
        {
            if (_configured) return;
            _configured = true;
            string v = null;
            try { v = Environment.GetEnvironmentVariable(EnvVar); } catch (Exception) { }
            v = v != null ? v.Trim().ToLowerInvariant() : null;
            Enabled = !(v == "off" || v == "0" || v == "false");
            if (v != null && v.Contains("novoice")) VoiceOn = false;
            if (v != null && v.Contains("nosky")) S15SnSky.On = false;
            DeLog.Info("DE.S15 config (Subnautica): " + (Enabled ? "on" : "OFF (" + EnvVar + "=" + v + ")") + "; voice " + VoiceOn + ", sky " + S15SnSky.On + "; " +
                       S15Conventions.Header + " " + DeLog.Hex(S15Conventions.Hash()) + (S15Conventions.Hash() == S15Conventions.PinnedHash ? " pinned" : " MISMATCH"));
            if (Enabled) S15SnStory.Patch();
        }

        /// <summary>Bridge hook: once per frame. <paramref name="sectionBase"/> = the shared section (null when detached). Never throws.</summary>
        public static void Frame(ulong nowMs, bool inWorld, bool seAlive, bool seStateValid, ref SeState st, byte* sectionBase, ulong presentedSeTick)
        {
            try
            {
                LoadConfig();
                if (!Enabled) return;
                if (!inWorld)
                {
                    if (_inWorldLast)
                    {
                        Voice.Reset();
                        _resolved = false;
                        _loggedSources = false;
                        _giveUpS = -1f;
                        for (int i = 0; i < _lines.Length; i++) { _lines[i] = null; _lineSource[i] = null; }
                        _seamothDone = false;
                        _s16VoicesSeen = -1;
                        _alertRow = false;
                        Playing.Clear();
                        S15SnVoiceClock.Reset();
                        S15SnPicks.Reset();
                    }
                    _inWorldLast = false;
                    S15SnSky.Frame(false, false, null, presentedSeTick);
                    return;
                }
                _inWorldLast = true;
                S15SnSky.Frame(true, seAlive, sectionBase, presentedSeTick, st.worldEpoch, seAlive && seStateValid);
                float now = Time.unscaledTime;
                WatchLines(now);
                AlertRow(now);
                // S16's hull voice heard this frame: our next line waits for it (one voice at a time, format voice.mix)
                if (_s16VoicesSeen >= 0 && S16Sn.Voices > _s16VoicesSeen) Voice.Heard(now);
                _s16VoicesSeen = S16Sn.Voices;
                S15WireState w = default(S15WireState);
                bool have = seAlive && seStateValid && S15Wire.TryRead(ref st, out w);
                // the events block (C6 region +0x700, its own seqlock: the sonar); a torn read is caught up next frame
                S15EventsState ev = null;
                uint evSeq;
                if (seAlive && sectionBase != null &&
                    S15EventsWire.TryRead(Layout.RegionPtr(sectionBase, RegionId.C6SeTime), Events, out evSeq) == SeqlockReadStatus.Fresh && Events.Valid) ev = Events;
                bool live = S15VoiceGate.SnAudioLive(st.snBusTarget) || DevMutants.IsOn(S15SnMutants.VoiceIgnoresS2);
                S15SnPicks.Frame(now, ev, live);
                S15SnLine line;
                if (!Voice.Step(have, ref w, ev, now, out line)) return;
                if (!_resolved) Resolve(now);
                Play(line, live, st.snBusTarget, now);
            }
            catch (Exception e)
            {
                Errors++;
                float t = Time.unscaledTime;
                if (t >= _nextErrorS) { _nextErrorS = t + 10f; DeLog.Warn("DE.Error S15 frame " + e.GetType().Name + ": " + e.Message); }
            }
        }

        private static void Play(S15SnLine line, bool live, float snBus, float now)
        {
            S15Conventions.VoiceRow row = S15Conventions.VoiceOf(line.Event);
            FMODAsset a = _lines[line.Event];
            string reason;
            bool played = false;
            float volume = S16VoiceMix.VoiceVolume;
            if (!VoiceOn) { reason = "voice off"; Suppressed++; }
            else if (!live) { reason = "seAudio"; Suppressed++; }
            else if (a == null) { reason = "missing"; Silent++; }
            else
            {
                Camera cam = MainCamera.camera;
                Vector3 pos = cam != null ? cam.transform.position : Vector3.zero;
                EventInstance inst = FMODUWE.GetEvent(a);   // the instance PlayOneShot would start
                if (inst.isValid())
                {
                    inst.setVolume(volume);
                    inst.set3DAttributes(pos.To3DAttributes());
                    inst.start();
                    inst.release();   // freed by FMOD at its end, as PlayOneShot's; the handle stays readable until then
                    var watch = new S15SnLineWatch { Event = line.Event, Seq = line.Seq, StartS = now, StartedUnder = S15SnMix.LastS2 >= 0.5f };
                    EventDescription desc;
                    int len;
                    if (inst.getDescription(out desc) == FMOD.RESULT.OK && desc.getLength(out len) == FMOD.RESULT.OK) watch.LengthMs = len;
                    Playing.Add(new KeyValuePair<EventInstance, S15SnLineWatch>(inst, watch));
                }
                else FMODUWE.PlayOneShot(a, pos, volume);
                played = true;
                reason = "played";
                Played++;
                Voice.Heard(now);
                S15SnVoiceClock.LastS15S = now;   //  S16's hull voice waits for it, S16's creaks duck under it
            }
            JsonOut j = S5Rows.Begin("s15.voice");
            j.Str("event", row.Name).Num("evId", (long)line.Event).Num("seq", (long)line.Seq).Bool("played", played).Str("reason", reason).Num("volume", (double)volume)
             .Num("snBus", (double)snBus).Num("queuedMs", (double)((now - line.QueuedS) * 1000.0)).Str("source", _lineSource[line.Event] ?? row.Owner + "." + row.Member)
             .Str("path", a != null ? a.path ?? "?" : "missing").Num("coalesced", Voice.Coalesced).Num("dropped", Voice.Dropped)
             .Bool("mutant", DevMutants.IsOn(S15SnMutants.VoiceIgnoresS2));
            S5Rows.End(j);
            DeLog.Info("DE.S15 sound " + row.Name + " seq=" + line.Seq.ToString(CultureInfo.InvariantCulture) + " played=" + (played ? "1" : "0") + " reason=" + reason +
                       " volume=" + volume.ToString("0.00", CultureInfo.InvariantCulture) + " snBus=" + snBus.ToString("0.00", CultureInfo.InvariantCulture) +
                       " path=" + (a != null ? a.path ?? "?" : "-"));
        }

        /// <summary>format voice.end: every played line followed to its end (the voice group's level, the mute, the surface).</summary>
        private static void WatchLines(float now)
        {
            for (int i = Playing.Count - 1; i >= 0; i--)
            {
                EventInstance inst = Playing[i].Key;
                S15SnLineWatch w = Playing[i].Value;
                PLAYBACK_STATE ps = PLAYBACK_STATE.STOPPED;
                int pos = 0;
                bool valid = inst.isValid() && inst.getPlaybackState(out ps) == FMOD.RESULT.OK;
                if (valid && inst.getTimelinePosition(out pos) == FMOD.RESULT.OK) w.Sample(pos, S15SnMix.LastVoice, S15SnMix.LastMuted, S15SnMix.LastS2);
                else w.Sample(0, S15SnMix.LastVoice, S15SnMix.LastMuted, S15SnMix.LastS2);
                bool over = !valid || ps == PLAYBACK_STATE.STOPPED || now - w.StartS > 30f;
                if (!over) continue;
                Playing.RemoveAt(i);
                Ended++;
                if (w.Complete) EndedComplete++;
                S15Conventions.VoiceRow row = S15Conventions.VoiceOf(w.Event);
                JsonOut j = S5Rows.Begin("s15.voiceend");
                j.Str("event", row != null ? row.Name : "?").Num("seq", (long)w.Seq).Num("heardMs", (double)((now - w.StartS) * 1000.0)).Num("lenMs", (long)w.LengthMs)
                 .Num("posMs", (long)w.LastPosMs).Bool("complete", w.Complete).Num("voiceMin", (double)w.VoiceMin).Bool("crossed", w.Crossed).Bool("startedUnder", w.StartedUnder)
                 .Bool("muted", w.Muted).Bool("groups", S15SnMix.Active).Bool("mutant", DevMutants.IsOn(S15SnMutants.MusicCut));
                S5Rows.End(j);
                if (w.Crossed) DeLog.Info("DE.S15 line " + (row != null ? row.Name : "?") + " crossed the surface: voice group min " + w.VoiceMin.ToString("0.00", CultureInfo.InvariantCulture) +
                                          (w.Muted ? ", MUTED" : "") + ", " + (w.Complete ? "complete" : "cut at " + w.LastPosMs + " of " + w.LengthMs + " ms"));
            }
        }

        /// <summary>The Cyclops's and the Seamoth's lines, read once per world (retried every 5 s for 60 s while a prefab is not loaded).</summary>
        private static void Resolve(float now)
        {
            if (now < _nextResolveS) return;
            _nextResolveS = now + 5f;
            if (_giveUpS < 0f) _giveUpS = now + 60f;
            GameObject cyclops = LightmappedPrefabs.main != null ? LightmappedPrefabs.main.GetScenePrefab("cyclops") : null;
            SubRoot sub = cyclops != null ? cyclops.GetComponentInChildren<SubRoot>(true) : null;
            bool all = true;
            for (int ev = 1; ev <= S15Conventions.Event.Count; ev++)
            {
                if (_lines[ev] != null) continue;
                S15Conventions.VoiceRow r = S15Conventions.VoiceOf(ev);
                if (r.Prefab == "seamoth") { if (!_seamothDone) { all = false; StartSeamoth(); } continue; }
                VoiceNotification vn = sub != null ? NotificationOf(sub, ev) : null;
                FMODAsset found = vn != null ? vn.sound : null;
                if (found != null) { _lines[ev] = found; _lineSource[ev] = "SubRoot." + r.Member; }
                else all = false;
            }
            if (!all && now < _giveUpS) return;
            _resolved = true;
            if (_loggedSources) return;
            _loggedSources = true;
            for (int ev = 1; ev <= S15Conventions.Event.Count; ev++)
            {
                S15Conventions.VoiceRow row = S15Conventions.VoiceOf(ev);
                if (_lines[ev] != null) DeLog.Info("DE.S15 sound " + row.Name + " = " + _lineSource[ev] + " path=" + _lines[ev].path);
                else DeLog.Warn("DE.S15 MISSING " + row.Owner + "." + row.Member + " of the " + row.Prefab + " prefab" + (sub == null && row.Prefab == "cyclops" ? " (not loaded)" : "") + ": the " + row.Name + " line stays silent");
            }
        }

        private static VoiceNotification NotificationOf(SubRoot sub, int ev)
        {
            switch (ev)
            {
                case S15Conventions.Event.Welcome: return sub.welcomeNotification;
                case S15Conventions.Event.EnginePowerUp: return sub.enginePowerUpNotification;
                case S15Conventions.Event.EnginePowerDown: return sub.enginePowerDownNotification;
                default: return null;
            }
        }

        private static void StartSeamoth()
        {
            if (_seamothLoading) return;
            _seamothLoading = true;
            try { CoroutineHost.StartCoroutine(LoadSeamoth()); }
            catch (Exception e) { _seamothLoading = false; _seamothDone = true; DeLog.Warn("DE.S15 Seamoth line: StartCoroutine " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>The Seamoth's own boarding line: Vehicle.welcomeNotification, else Vehicle.enterSound, else SeaMoth.enterSeamoth.</summary>
        private static IEnumerator LoadSeamoth()
        {
            CoroutineTask<GameObject> task = CraftData.GetPrefabForTechTypeAsync(TechType.Seamoth, false);
            yield return task;
            _seamothLoading = false;
            _seamothDone = true;
            try
            {
                GameObject prefab = task.GetResult();
                SeaMoth sm = prefab != null ? prefab.GetComponentInChildren<SeaMoth>(true) : null;
                FMODAsset a = null;
                string src = null;
                if (sm != null && sm.welcomeNotification != null && sm.welcomeNotification.sound != null) { a = sm.welcomeNotification.sound; src = "Vehicle.welcomeNotification"; }
                else if (sm != null && sm.enterSound != null) { a = sm.enterSound; src = "Vehicle.enterSound (no welcome line on the prefab)"; }
                else if (sm != null && sm.enterSeamoth != null && sm.enterSeamoth.asset != null) { a = sm.enterSeamoth.asset; src = "SeaMoth.enterSeamoth (no welcome line, no enter sound)"; }
                _lines[S15Conventions.Event.SmallWelcome] = a;
                _lineSource[S15Conventions.Event.SmallWelcome] = src;
                if (a != null) DeLog.Info("DE.S15 sound SmallWelcome = Seamoth " + src + " path=" + a.path +
                                          (sm.noPowerWelcomeNotification != null && sm.noPowerWelcomeNotification.sound != null ? " (no-power line " + sm.noPowerWelcomeNotification.sound.path + ")" : ""));
                else DeLog.Warn("DE.S15 MISSING the Seamoth's boarding line (" + (prefab == null ? "no Seamoth prefab" : sm == null ? "no SeaMoth component" : "no welcome line, enter sound or enter emitter") + "): SmallWelcome stays silent");
            }
            catch (Exception e) { DeLog.Warn("DE.S15 Seamoth line " + e.GetType().Name + ": " + e.Message); }
        }

        /// </summary>
        private static void AlertRow(float now)
        {
            if (_alertRow || now < _nextAlertS) return;
            _nextAlertS = now + 5f;
            Player p = Player.main;
            LowOxygenAlert a = p != null ? p.GetComponentInChildren<LowOxygenAlert>(true) : null;
            if (a == null) a = UnityEngine.Object.FindObjectOfType<LowOxygenAlert>();
            if (a == null && p == null) return;
            _alertRow = true;
            JsonOut j = S5Rows.Begin("s15.alert");
            j.Bool("found", a != null).Bool("enabled", a != null && a.enabled).Num("alerts", a != null && a.alertList != null ? (long)a.alertList.Count : -1L);
            S5Rows.End(j);
            DeLog.Info("DE.S15 the game's LowOxygenAlert " + (a == null ? "not found" : a.enabled ? "enabled (Subnautica's own alert, untouched)" : "DISABLED by something else"));
        }

        public static string Status()
        {
            var parts = new List<string>();
            for (int ev = 1; ev <= S15Conventions.Event.Count; ev++) parts.Add(S15Conventions.VoiceOf(ev).Name + (_lines[ev] != null ? "" : " (missing)"));
            return "voice " + (VoiceOn ? "on" : "off") + ": played " + Played + ", suppressed " + Suppressed + ", silent " + Silent + ", waiting " + Voice.Waiting +
                   ", dropped " + Voice.Dropped + ", ended " + Ended + " (" + EndedComplete + " complete) [" + string.Join(", ", parts.ToArray()) + "] | " + S15SnMix.Status() +
                   " | " + S15SnStory.Status() + " | " + S15SnSky.Status() + " | " + S15SnPicks.Status();
        }

        // ============================================================================================== de.s15

 /// <summary>
 /// <c>de.s15 {status} | {voice: on|off} | {sky: on|off} | {time: c} | {music: 0..1} | {voiceAbove: 0..1} | {story: aurora1..4|key}</c>
 /// (chat <c>/de sn s15 music 0.6</c>); dev builds.
 /// </summary>
        public static bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            if (req == null || req.Cmd != Verb) return false;
            try
            {
                if (!BuildFlavor.Dev) { reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", UserCommandRules.RefusalText); return true; }
                Dictionary<string, object> a = S3Args.ToDict(req.Args);
                object v;
                if (a.TryGetValue("argv", out v) && v is System.Collections.IList)
                {
                    var l = (System.Collections.IList)v;
                    if (l.Count >= 2) a[Convert.ToString(l[0], CultureInfo.InvariantCulture)] = Convert.ToString(l[1], CultureInfo.InvariantCulture);
                }
                string error = null, storyKey = null;
                if (a.TryGetValue("voice", out v)) error = OnOff(v, ref VoiceOn, "voice");
                if (a.TryGetValue("sky", out v)) { bool on = S15SnSky.On; error = OnOff(v, ref on, "sky") ?? error; S15SnSky.On = on; }
                if (error == null && a.TryGetValue("music", out v)) error = Level(v, ref S15SnMix.Groups.MusicAbove, "music");
                if (error == null && a.TryGetValue("voiceAbove", out v)) error = Level(v, ref S15SnMix.Groups.VoiceAbove, "voiceAbove");
                if (error == null && a.TryGetValue("story", out v)) error = S15SnStory.Trigger(Convert.ToString(v, CultureInfo.InvariantCulture), out storyKey);
                if (error == null && a.TryGetValue("time", out v))
                {
                    // the launcher's clock (S7's time sync is off in the test world: Subnautica keeps its own clock, run
 // 20261024-054404): DayNightCycle.SetDayNightTime(scalar), 0 midnight, 0.5 noon
                    double c;
                    if (!double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out c)) error = "time: 0..1";
                    else if (DayNightCycle.main == null) error = "no DayNightCycle";
                    else
                    {
                        DayNightCycle.main.SetDayNightTime((float)c);
                        DeLog.Info("DE.S15 Subnautica's day set to " + c.ToString("0.###", CultureInfo.InvariantCulture) + " (de.s15 time)");
                    }
                }
                if (error != null) { reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", error); return true; }
                string line = "DE.S15 " + Status();
                // the launcher aims its captures with these: uSky's moon axis and the shown discs (SN directions, degrees)
                Vector3 ma = S15SnSky.MoonAxis();
                var js = new System.Text.StringBuilder("[");
                foreach (object o in S15SnSky.ShownBodies())
                {
                    var b = (Dictionary<string, object>)o;
                    var dir = (List<object>)b["dir"];
                    if (js.Length > 1) js.Append(',');
                    js.Append("{\"name\":");
                    Json.AppendString(js, (string)b["name"]);
                    js.Append(",\"angDeg\":").Append(((double)b["angDeg"]).ToString("R", CultureInfo.InvariantCulture)).Append(",\"dir\":[")
                      .Append(((double)dir[0]).ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(((double)dir[1]).ToString("R", CultureInfo.InvariantCulture))
                      .Append(',').Append(((double)dir[2]).ToString("R", CultureInfo.InvariantCulture)).Append("]}");
                }
                js.Append(']');
                reply.Bool("ok", true).Str("cmd", req.Cmd).Bool("voice", VoiceOn).Bool("sky", S15SnSky.On).Num("played", Played).Num("ended", Ended).Num("bodies", (long)S15SnSky.Shown)
                     .Bool("moonsHidden", S15SnSky.MoonsHidden).Num("s16Held", S15SnVoiceClock.S16Held).Bool("groups", S15SnMix.Active)
                     .Num("musicAbove", (double)S15SnMix.Groups.MusicAbove).Num("voiceAbove", (double)S15SnMix.Groups.VoiceAbove).Num("storySkipped", S15SnStory.Skipped)
                     .Str("storyKey", storyKey)
                     .Raw("moonAxis", "[" + ma.x.ToString("R", CultureInfo.InvariantCulture) + "," + ma.y.ToString("R", CultureInfo.InvariantCulture) + "," + ma.z.ToString("R", CultureInfo.InvariantCulture) + "]")
                     .Raw("shown", js.ToString()).Str("line", line);
                return true;
            }
            catch (Exception e)
            {
                reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", e.GetType().Name + ": " + e.Message);
                return true;
            }
        }

        private static string OnOff(object v, ref bool field, string key)
        {
            string s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
            if (s != "on" && s != "off") return key + ": on|off";
            field = s == "on";
            DeLog.Info("DE.S15 " + key + " " + s);
            return null;
        }

        /// <summary>de.s15 {music|voiceAbove: 0.. format audio.groups).</summary>
        private static string Level(object v, ref float field, string key)
        {
            double x;
            if (!double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out x) || !(x >= 0 && x <= 1)) return key + ": 0..1";
            field = (float)x;
            DeLog.Info("DE.S15 " + key + " above water " + x.ToString("0.###", CultureInfo.InvariantCulture) + " (de.s15; the pinned " +
                       (key == "music" ? S15Conventions.Mix.MusicAbovePermille : S15Conventions.Mix.VoiceAbovePermille) / 1000.0 + ")");
            return null;
        }
    }
}
