using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// md items 8, 9, 3, 5, 11; CONTRACT §5.4 "S15
 /// conventions", formats night.lights, lens.splash, sonar.ping, seabase.room, fish.scare; the first-encounter lines of item
 /// Main thread
 /// once per frame from <see cref="S15Sn.Frame"/> while in the world with SE alive. Every member used is VERIFIED in the
 /// decompiled sources (file:line at the use). Never throws (S15Sn's guard).
 /// </summary>
    internal static class S15SnPicks
    {
        private static readonly S15SnSplash Splash = new S15SnSplash();
        private static readonly S16SeqReader SonarReader = new S16SeqReader();
        private static readonly List<EcoTarget> Proxies = new List<EcoTarget>();
        private static readonly List<Scareable> Fish = new List<Scareable>();
        private static readonly Dictionary<int, Vector3> LastProxyPos = new Dictionary<int, Vector3>();
        private static readonly List<FMODGameParams> Switched = new List<FMODGameParams>();
        private static int _humCandidates;
        private static FMODAsset _sonarSound;
        private static bool _sonarResolved, _inside;
        private static GameObject _lightGo;
        private static float _nextListS, _nextFishS, _nextNightRowS, _nextSeabaseRowS, _lastFishS;
        private static float _nightFactor = 1f, _lightScalar = 1f;
        public static long Splashes, SonarPings, FishScared, SeabaseEntries;

        public static void Reset()
        {
            Splash.Reset();
            SonarReader.Reset();
            Proxies.Clear();
            Fish.Clear();
            LastProxyPos.Clear();
            _sonarSound = null;
            _sonarResolved = false;
            Leave();
            _nextListS = _nextFishS = _nextNightRowS = _nextSeabaseRowS = 0f;
            _nightFactor = 1f;
        }

        /// <summary>
 /// One frame: <paramref name="ev"/> = the C6 events block when fresh (else null), <paramref name="live"/> = Subnautica's
 /// audio live (the S2 rule).
 /// </summary>
        public static void Frame(float now, S15EventsState ev, bool live)
        {
            Camera cam = MainCamera.camera;
            if (cam == null) return;
            Vector3 camPos = cam.transform.position;
            if (now >= _nextListS) { _nextListS = now + 5f; RefreshLists(); }
            Night(now);
            SplashStep(cam, camPos, now);
            if (ev != null && ev.Valid) Sonar(now, ev, camPos, live);
            Seabase(cam, camPos, now);
            if (now >= _nextFishS) { _nextFishS = now + S15Conventions.Picks.FishScanMs / 1000f; FishStep(now); }
        }

        // ============================================================================================ lists (every 5 s)

        private static void RefreshLists()
        {
            Proxies.Clear();
            foreach (EcoTarget t in UnityEngine.Object.FindObjectsOfType<EcoTarget>())
            {
                if (t == null) continue;
                if (t.gameObject.name.StartsWith("DE.Proxy ", StringComparison.Ordinal)) Proxies.Add(t);   // S6's ship sections (ProxySections.AddEco)
            }
            Fish.Clear();
            foreach (Scareable s in UnityEngine.Object.FindObjectsOfType<Scareable>())
                if (s != null && s.creature != null && s.creatureFear != null && s.GetComponent<FleeWhenScared>() != null) Fish.Add(s);
        }

        // ============================================================================================ 8: night

 /// <summary>The LightMirror hook: the factor of a mirrored light of this kind (0 BlockPoint, 1 BlockSpot: dimmed at night).</summary>
        public static float LightFactor(int kind) { return kind <= 1 ? _nightFactor : 1f; }

        private static void Night(float now)
        {
            DayNightCycle dnc = DayNightCycle.main;
            _lightScalar = dnc != null ? dnc.GetLightScalar() : 1f;
            bool off = DevMutants.IsOn(S15SnMutants.NoNight);
            _nightFactor = off ? 1f : (float)S15PickRules.NightFactor(_lightScalar);
            if (now < _nextNightRowS) return;
            _nextNightRowS = now + 5f;
            JsonOut j = S5Rows.Begin("s15.night");
            j.Num("light", (double)_lightScalar).Num("factor", (double)_nightFactor).Num("dayScalar", dnc != null ? (double)dnc.GetDayScalar() : double.NaN).Bool("mutant", off);
            S5Rows.End(j);
        }

        // ============================================================================================ 9: the lens

        private static void SplashStep(Camera cam, Vector3 camPos, float now)
        {
            if (!Splash.Step(camPos.y, now)) return;
            bool off = DevMutants.IsOn(S15SnMutants.NoSplash);
            LensWater lens = cam.GetComponent<LensWater>();
            bool created = false;
            if (!off && lens != null) { lens.CreateSplash(); created = true; Splashes++; }
            JsonOut j = S5Rows.Begin("s15.splash");
            j.Bool("created", created).Bool("lens", lens != null).Num("y", (double)camPos.y).Bool("mutant", off);
            S5Rows.End(j);
            DeLog.Info("DE.S15 splash " + (created ? "on the lens" : off ? "skipped (S15.M12)" : "MISSING (no LensWater on the camera)"));
        }

        // ============================================================================================ 3: sonar

        private static void Sonar(float now, S15EventsState ev, Vector3 camPos, bool live)
        {
            int n = SonarReader.Step(ev.WorldEpoch, ev.SonarSeq);
            if (n <= 0) return;
            if (!_sonarResolved)
            {
                _sonarResolved = true;
                GameObject cyclops = LightmappedPrefabs.main != null ? LightmappedPrefabs.main.GetScenePrefab("cyclops") : null;
                CyclopsSonarButton b = cyclops != null ? cyclops.GetComponentInChildren<CyclopsSonarButton>(true) : null;
                _sonarSound = b != null && b.soundFX != null ? b.soundFX.asset : null;
                DeLog.Info("DE.S15 sonar sound = CyclopsSonarButton.soundFX.asset path=" + (_sonarSound != null ? _sonarSound.path : "MISSING"));
            }
            bool noFx = DevMutants.IsOn(S15SnMutants.SonarNoFx);
            bool played = false, fx = false;
            string reason;
            if (!live) reason = "seAudio";
            else
            {
                if (_sonarSound != null) { FMODUWE.PlayOneShot(_sonarSound, camPos, 1f); played = true; }
                if (!noFx && SNCameraRoot.main != null) { SNCameraRoot.main.SonarPing(); fx = true; }
                reason = played || fx ? "played" : "missing";
                SonarPings++;
            }
            JsonOut j = S5Rows.Begin("s15.sonar");
            j.Num("seq", (long)ev.SonarSeq).Bool("played", played).Bool("fx", fx).Str("reason", reason).Str("path", _sonarSound != null ? _sonarSound.path : "missing").Bool("mutant", noFx);
            S5Rows.End(j);
        }

        // ============================================================================================ 5: the seabase

        private static void Seabase(Camera cam, Vector3 camPos, float now)
        {
            bool inside = S7Interior.InsideNow && camPos.y < 0f;
            if (inside && !_inside) Enter(cam);
            else if (!inside && _inside) Leave();
            _inside = inside;
            if (inside && now >= _nextSeabaseRowS) { _nextSeabaseRowS = now + 5f; SeabaseRow("inside"); }
        }

        private static void Enter(Camera cam)
        {
            SeabaseEntries++;
            Switched.Clear();
            _humCandidates = 0;
            bool noHum = DevMutants.IsOn(S15SnMutants.NoHum);
            foreach (FMODGameParams p in UnityEngine.Object.FindObjectsOfType<FMODGameParams>())
                if (p != null && p.interiorState == FMODGameParams.InteriorState.OnlyInBase)
                {
                    _humCandidates++;
                    if (noHum) continue;
                    p.interiorState = FMODGameParams.InteriorState.OnlyInside;   // answered by S7's IsInsideWalkable
                    Switched.Add(p);
                }
            if (_lightGo == null)
            {
                _lightGo = new GameObject("DE.S15 seabase light");
                UnityEngine.Object.DontDestroyOnLoad(_lightGo);
                Light l = _lightGo.AddComponent<Light>();
                l.type = LightType.Point;
                BandRgb c = S15Conventions.Picks.SeabaseLight;
                l.color = new Color(c.R / 255f, c.G / 255f, c.B / 255f, 1f);
                l.intensity = S15Conventions.Picks.SeabaseLightPermille / 1000f;
                l.range = S15Conventions.Picks.SeabaseLightRangeDm / 10f;
                l.shadows = LightShadows.None;
            }
            _lightGo.transform.SetParent(cam.transform, false);
            _lightGo.transform.localPosition = Vector3.zero;
            _lightGo.SetActive(true);
            SeabaseRow("enter");
        }

        private static void Leave()
        {
            foreach (FMODGameParams p in Switched)
                if (p != null) p.interiorState = FMODGameParams.InteriorState.OnlyInBase;
            int n = Switched.Count;
            Switched.Clear();
            if (_lightGo != null)
            {
                _lightGo.transform.SetParent(null, false);
                _lightGo.SetActive(false);
            }
            if (_inside) SeabaseRow("leave", n);
            _inside = false;
        }

        private static void SeabaseRow(string what, int restored = 0)
        {
            var paths = new System.Text.StringBuilder();
            for (int i = 0; i < Switched.Count && i < 3; i++)
            {
                FMODGameParams p = Switched[i];
                FMODAsset a = p != null && p.loopingEmitter != null ? p.loopingEmitter.asset : null;
                if (paths.Length > 0) paths.Append(',');
                paths.Append(a != null ? a.path : "?");
            }
            JsonOut j = S5Rows.Begin("s15.seabase");
            j.Str("what", what).Bool("inside", _inside || what == "enter").Num("hum", (long)Switched.Count).Num("candidates", (long)_humCandidates).Num("restored", (long)restored).Str("paths", paths.ToString())
             .Bool("light", _lightGo != null && _lightGo.activeSelf).Bool("mutant", DevMutants.IsOn(S15SnMutants.NoHum));
            S5Rows.End(j);
            if (what != "inside") DeLog.Info("DE.S15 seabase " + what + ": base hum loops " + (what == "leave" ? restored + " restored" : Switched.Count + " switched [" + paths + "]") + ", light " + (_lightGo != null && _lightGo.activeSelf ? "on" : "off"));
        }

        // ============================================================================================ 11: fish

        private static void FishStep(float now)
        {
            float dt = _lastFishS > 0f ? now - _lastFishS : 0f;
            _lastFishS = now;
            if (dt <= 0f || dt > 5f) { LastProxyPos.Clear(); RememberProxies(); return; }
            bool off = DevMutants.IsOn(S15SnMutants.NoScare);
            float range = S15Conventions.Picks.FishScareRangeM;
            int fast = 0, scared = 0, near = 0, jumps = 0;
            double maxSpeed = 0;
            foreach (EcoTarget t in Proxies)
            {
                if (t == null) continue;
                int id = t.GetInstanceID();
                Vector3 p = t.transform.position;
                Vector3 was;
                bool had = LastProxyPos.TryGetValue(id, out was);
                LastProxyPos[id] = p;
                if (!had || p.y > 0f) continue;
                double speed = (p - was).magnitude / dt;
                if (S15PickRules.Jump(speed)) { jumps++; continue; }   // a teleport or a re-pose, not a ship's speed
                if (speed > maxSpeed) maxSpeed = speed;
                if (!S15PickRules.Scares(speed)) continue;
                fast++;
                foreach (Scareable s in Fish)
                {
                    if (s == null || (s.transform.position - p).sqrMagnitude > range * range) continue;
                    near++;
                    if (off) continue;
                    s.creature.Scared.Add(S15Conventions.Picks.FishScarePermille / 1000f);
                    s.creatureFear.SetScarePosition(p, range);
                    FleeWhenScared flee = s.GetComponent<FleeWhenScared>();
                    if (flee != null) s.creature.TryStartAction(flee);
                    scared++;
                }
            }
            if (fast == 0 && scared == 0) return;
            FishScared += scared;
            JsonOut j = S5Rows.Begin("s15.fish");
            j.Num("fast", (long)fast).Num("jumps", (long)jumps).Num("maxSpeed", maxSpeed).Num("near", (long)near).Num("scared", (long)scared).Num("fish", (long)Fish.Count).Bool("mutant", off);
            S5Rows.End(j);
        }

        private static void RememberProxies()
        {
            foreach (EcoTarget t in Proxies) if (t != null) LastProxyPos[t.GetInstanceID()] = t.transform.position;
        }

        public static string Status()
        {
            return "picks: night x" + _nightFactor.ToString("0.00", CultureInfo.InvariantCulture) + " (light " + _lightScalar.ToString("0.00", CultureInfo.InvariantCulture) + "), splashes " + Splashes +
                   ", sonar " + SonarPings + ", seabase " + (_inside ? "inside (" + Switched.Count + " hum loops)" : "out") + ", fish scared " + FishScared +
                   ", proxies " + Proxies.Count;
        }
    }
}
