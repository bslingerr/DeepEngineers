using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DeepEngineers.SN
{
    /// <summary>
 /// SN lore shouldn't be in
 /// SE"; ROADMAP §8 landmark row, S7-STATUS O-9). Default hidden; <c>DEEPENGINEERS_SN_LANDMARKS=on</c> shows them at
 /// start; <c>de.s7 {landmarks: "on" | "off" | "status"}</c> (hook in <see cref="S7aHost"/>) and <c>de.landmarks {state:
 /// on | off | status}</c> (routed by <see cref="S7Host"/>) switch at run time to compare. What it hides, found at run time
 /// (re-discovered every 2 s, so a world load or a re-bind is covered; the hierarchies re-scanned every 30 s):
 /// <list type="bullet">
 /// <item>scene roots whose name contains "aurora"
 /// never a root of ours ("DE.", "DeepEngineers.");</item>
 /// <item><c>CrashedShipExploder.main</c>: its
 /// <c>explodedExterior</c>, <c>disableOnExplosion[]</c>, <c>enableOnExplosion[]</c> (public GameObject fields, <c>:17–24</c>)
 /// and the root it lives under when that root's name contains "aurora"; the component itself disabled (its
 /// <c>Update</c> runs the countdown, the explosion, the camera shake and the 2000-damage radius blast, <c>:146–176</c>);</item>
 /// <item>in each hidden hierarchy: every <c>Renderer</c> (meshes, skinned meshes, particle renderers), <c>Collider</c>
 /// (also triggers: the crashedShip atmosphere volumes), <c>Light</c>, <c>FMOD_CustomEmitter</c>
 /// and <c>FMOD_StudioEventEmitter</c>
 /// </item>
 /// <item>the radiation story: <c>LeakingRadiation.main</c> disabled
 /// its <c>damagePlayerInRadius</c> disabled
 /// its <c>radiatePlayerInRange</c>'s <c>InvokeRepeating("Radiate")</c> cancelled
 /// and <c>Player.main.SetRadiationAmount(0)</c>
 /// <c>CrashedShipAmbientSound.main</c>'s <c>CheckPlayerPosition</c> invoke cancelled and
 /// <c>isPlayerInside</c> cleared; the <c>AuroraWarnings</c> component
 /// disabled.</item>
 /// </list>
 /// Everything disabled is remembered and re-enabled on "on" (and when the host is disposed: the bridge leaves Subnautica
 /// as it found it). Not reachable here (reported in S7-STATUS): the exploded debris fields and the Aurora interior rooms
 /// placed as separate world entities in the cells (<c>WorldEntities/Doodads/Debris/Wrecks/*aurora*</c>, the
 /// <c>Starship_*</c> props), story goals and PDA entries already triggered, the "crashedShip" biome string of the
 /// map, the main-menu Aurora. The statics reporter skips a hidden hierarchy's colliders (<see cref="Covers"/>) and drops
 /// the primitives it sent of them when they are hidden again (<see cref="Version"/>), so SE keeps no collision for them.
 /// Main thread only. Any exception turns the toggle off for the session (logged once); the bridge keeps running.
 /// </summary>
    internal static class S7Landmarks
    {
        public const string EnvVar = "DEEPENGINEERS_SN_LANDMARKS";
        public const string Cmd = "de.landmarks";
        public const ulong DiscoverEveryMs = 2000, RescanEveryMs = 30000;

        /// <summary>True while the landmarks are hidden.</summary>
        public static bool Hidden { get; private set; } = ReadEnvHidden();
        /// <summary>+1 on every hide/show and on every newly hidden hierarchy (the statics reporter compares it).</summary>
        public static int Version { get; private set; }
        public static long Discovers, Applies, Restores, Errors;

        private sealed class Hier
        {
            public GameObject Go;
            public int Id;
            public string Name, Why;
            public bool Applied;
            public int Renderers, Colliders, Lights, Emitters;
        }

        private static readonly Dictionary<int, Hier> Hiers = new Dictionary<int, Hier>();
        private static readonly HashSet<int> HiddenIds = new HashSet<int>();
        private static readonly List<Renderer> OffRenderers = new List<Renderer>();
        private static readonly List<Collider> OffColliders = new List<Collider>();
        private static readonly List<Light> OffLights = new List<Light>();
        private static readonly List<Behaviour> OffBehaviours = new List<Behaviour>();
        private static RadiatePlayerInRange _radiateCancelled;
        private static CrashedShipAmbientSound _ambientCancelled;
        private static AuroraWarnings _warnings;
        private static ulong _nextDiscoverMs, _nextRescanMs, _nextWarningsLookupMs;
        private static bool _off;
        private static string _offWhy;
        private static bool _loggedState;

        private static bool ReadEnvHidden()
        {
            string v = null;
            try { v = Environment.GetEnvironmentVariable(EnvVar); } catch (Exception) { }
            if (string.IsNullOrEmpty(v)) return false;   // unset: shown
            v = v.Trim().ToLowerInvariant();
            // env "off"/"hide" hides it
            return v == "off" || v == "0" || v == "false" || v == "no" || v == "hide";
        }

        // ============================================================================================ per frame

 /// <summary>Per frame from the S7 host (main thread). Never throws.</summary>
        public static void Update(ulong nowMs)
        {
            if (_off) return;
            if (!_loggedState)
            {
                _loggedState = true;
                DeLog.Info("DE.S7 landmarks " + (Hidden ? "hidden" : "shown") + " (" + EnvVar + " " + (Environment.GetEnvironmentVariable(EnvVar) ?? "unset: default shown") +
                           "; de.s7 {landmarks: on|off}): the Aurora's scene roots, its exploder, radiation and warnings");
            }
            if (nowMs < _nextDiscoverMs) return;
            _nextDiscoverMs = nowMs + DiscoverEveryMs;
            try
            {
                bool rescan = nowMs >= _nextRescanMs;
                if (rescan) _nextRescanMs = nowMs + RescanEveryMs;
                Discover(nowMs);
                if (Hidden) ApplyAll(rescan);
            }
            catch (Exception e)
            {
                Errors++;
                _off = true;
                _offWhy = e.GetType().Name + ": " + e.Message;
                DeLog.Error("DE.S7 landmarks OFF after an exception (the toggle stops; everything else keeps running): " + e);
                try { RestoreAll("toggle off after an exception"); } catch (Exception) { }
            }
        }

        private static void Discover(ulong nowMs)
        {
            Discovers++;
            // the dead (a world unload destroys them): forgotten
            var dead = new List<int>();
            foreach (KeyValuePair<int, Hier> kv in Hiers) if (kv.Value.Go == null) dead.Add(kv.Key);
            foreach (int id in dead) { Hiers.Remove(id); HiddenIds.Remove(id); }
            if (dead.Count > 0) Prune();

            int n = SceneManager.sceneCount;
            for (int i = 0; i < n; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded) continue;
                GameObject[] roots = s.GetRootGameObjects();
                for (int k = 0; k < roots.Length; k++)
                {
                    GameObject r = roots[k];
                    if (r != null && IsAuroraName(r.name)) Track(r, "scene root '" + r.name + "' (" + s.name + ")");
                }
            }
            CrashedShipExploder x = CrashedShipExploder.main;
            if (x != null)
            {
                Transform root = x.transform.root;
                if (root != null && IsAuroraName(root.name)) Track(root.gameObject, "the exploder's root '" + root.name + "'");
                if (x.explodedExterior != null) Track(x.explodedExterior, "CrashedShipExploder.explodedExterior");
                if (x.disableOnExplosion != null) foreach (GameObject g in x.disableOnExplosion) if (g != null) Track(g, "CrashedShipExploder.disableOnExplosion");
                if (x.enableOnExplosion != null) foreach (GameObject g in x.enableOnExplosion) if (g != null) Track(g, "CrashedShipExploder.enableOnExplosion");
            }
            if (_warnings == null && nowMs >= _nextWarningsLookupMs)
            {
                _nextWarningsLookupMs = nowMs + RescanEveryMs; // FindObjectOfType walks every object: twice a minute at most
                _warnings = UnityEngine.Object.FindObjectOfType<AuroraWarnings>();
            }
        }

        private static bool IsAuroraName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.StartsWith("DE.", StringComparison.Ordinal) || name.StartsWith("DeepEngineers.", StringComparison.Ordinal)) return false;

            // (CrashedShipExploder, CrashedShipAmbientSound, the crashedShip atmosphere volumes)
            return name.IndexOf("aurora", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("crashedship", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("crashed_ship", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static long ParticlesSwept;

        /// <summary>
 /// The fire and smoke of the wreck can be particle systems under roots named otherwise (spawned VFX): every rescan, each
 /// ParticleSystem whose hierarchy path names the wreck and that is not ours is stopped and its renderer turned off (restored
 /// with the rest). Resources.FindObjectsOfTypeAll: a few hundred objects, twice a minute at most.
 /// </summary>
        private static void SweepParticles()
        {
            ParticleSystem[] all;
            try { all = Resources.FindObjectsOfTypeAll<ParticleSystem>(); }
            catch (Exception) { return; }
            for (int i = 0; i < all.Length; i++)
            {
                ParticleSystem ps = all[i];
                if (ps == null || ps.gameObject == null || !ps.gameObject.scene.IsValid()) continue;
                bool wreck = false, ours = false;
                for (Transform tr = ps.transform; tr != null; tr = tr.parent)
                {
                    string nm = tr.name;
                    if (nm.StartsWith("DE.", StringComparison.Ordinal) || nm.StartsWith("DeepEngineers.", StringComparison.Ordinal)) { ours = true; break; }
                    if (IsAuroraName(nm)) wreck = true;
                }
                if (ours || !wreck) continue;
                Renderer r = ps.GetComponent<Renderer>();
                if (r != null && r.enabled) { r.enabled = false; OffRenderers.Add(r); ParticlesSwept++; }
                try { ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); } catch (Exception) { }
            }
        }

        private static void Track(GameObject g, string why)
        {
            int id = g.GetInstanceID();
            if (Hiers.ContainsKey(id)) return;
            // a hierarchy already inside a tracked one is covered by it
            for (Transform t = g.transform.parent; t != null; t = t.parent) if (Hiers.ContainsKey(t.gameObject.GetInstanceID())) return;
            Hiers[id] = new Hier { Go = g, Id = id, Name = g.name, Why = why };
        }

        // ============================================================================================ hide / show

        private static void ApplyAll(bool rescan)
        {
            bool newly = false;
            foreach (Hier h in Hiers.Values)
            {
                if (h.Go == null) continue;
                if (h.Applied && !rescan) continue;
                bool first = !h.Applied;
                ApplyHierarchy(h);
                if (first)
                {
                    newly = true;
                    DeLog.Info("DE.S7 landmarks hidden: " + h.Why + " '" + h.Name + "': " + h.Renderers + " renderers, " + h.Colliders + " colliders, " +
                               h.Lights + " lights, " + h.Emitters + " sound emitters off");
                }
            }
            ApplyStory();
            if (newly || rescan) SweepParticles();
            if (newly) Version++;
        }

        private static void ApplyHierarchy(Hier h)
        {
            Applies++;
            h.Applied = true;
            HiddenIds.Add(h.Id);
            foreach (Renderer r in h.Go.GetComponentsInChildren<Renderer>(true))
                if (r != null && r.enabled) { r.enabled = false; OffRenderers.Add(r); h.Renderers++; }
            foreach (Collider c in h.Go.GetComponentsInChildren<Collider>(true))
                if (c != null && c.enabled) { c.enabled = false; OffColliders.Add(c); h.Colliders++; }
            foreach (Light l in h.Go.GetComponentsInChildren<Light>(true))
                if (l != null && l.enabled) { l.enabled = false; OffLights.Add(l); h.Lights++; }
            foreach (FMOD_CustomEmitter e in h.Go.GetComponentsInChildren<FMOD_CustomEmitter>(true))
                if (e != null && e.enabled) { e.enabled = false; OffBehaviours.Add(e); h.Emitters++; }
            foreach (FMOD_StudioEventEmitter e in h.Go.GetComponentsInChildren<FMOD_StudioEventEmitter>(true))
            {
                if (e == null || !e.enabled) continue;
                e.Stop(); // its OnDisable only deregisters the update: stopped first
                e.enabled = false;
                OffBehaviours.Add(e);
                h.Emitters++;
            }
        }

        /// <summary>The exploder, the radiation, the ambience check and the warnings (once each while hidden).</summary>
        private static void ApplyStory()
        {
            Off(CrashedShipExploder.main);
            LeakingRadiation lr = LeakingRadiation.main;
            if (lr != null)
            {
                Off(lr);
                Off(lr.damagePlayerInRadius);
                RadiatePlayerInRange rp = lr.radiatePlayerInRange;
                if (rp != null && !ReferenceEquals(rp, _radiateCancelled))
                {
                    rp.CancelInvoke();
                    _radiateCancelled = rp;
                    Off(rp);
                    Player p = Player.main;
                    if (p != null) p.SetRadiationAmount(0f);
                    DeLog.Info("DE.S7 landmarks: the Aurora's radiation stopped (LeakingRadiation, DamagePlayerInRadius, RadiatePlayerInRange)");
                }
            }
            CrashedShipAmbientSound amb = CrashedShipAmbientSound.main;
            if (amb != null && !ReferenceEquals(amb, _ambientCancelled))
            {
                amb.CancelInvoke();
                amb.isPlayerInside = false;
                _ambientCancelled = amb;
                Off(amb);
            }
            Off(_warnings);
        }

        private static void Off(Behaviour b)
        {
            if (b == null || !b.enabled) return;
            b.enabled = false;
            OffBehaviours.Add(b);
        }

        /// <summary>Re-enables everything this class switched off (show, host disposed, error).</summary>
        public static void RestoreAll(string why)
        {
            int n = 0;
            foreach (Renderer r in OffRenderers) if (r != null) { r.enabled = true; n++; }
            foreach (Collider c in OffColliders) if (c != null) { c.enabled = true; n++; }
            foreach (Light l in OffLights) if (l != null) { l.enabled = true; n++; }
            foreach (Behaviour b in OffBehaviours) if (b != null) { b.enabled = true; n++; }
            OffRenderers.Clear(); OffColliders.Clear(); OffLights.Clear(); OffBehaviours.Clear();
            if (_radiateCancelled != null) { _radiateCancelled.InvokeRepeating("Radiate", 0f, 0.2f); _radiateCancelled = null; }
            if (_ambientCancelled != null) { _ambientCancelled.InvokeRepeating("CheckPlayerPosition", 0f, 1f); _ambientCancelled = null; }
            foreach (Hier h in Hiers.Values) { h.Applied = false; h.Renderers = h.Colliders = h.Lights = h.Emitters = 0; }
            HiddenIds.Clear();
            if (n > 0) Restores++;
            if (n > 0) DeLog.Info("DE.S7 landmarks shown again (" + why + "): " + n + " components re-enabled");
        }

        private static void Prune()
        {
            OffRenderers.RemoveAll(x => x == null);
            OffColliders.RemoveAll(x => x == null);
            OffLights.RemoveAll(x => x == null);
            OffBehaviours.RemoveAll(x => x == null);
        }

        /// <summary>Hide (true) or show (false); logged; the statics reporter follows through <see cref="Version"/>.</summary>
        public static void Set(bool hidden, string why)
        {
            if (_off) return;
            if (hidden == Hidden) return;
            Hidden = hidden;
            Version++;
            if (hidden)
            {
                DeLog.Info("DE.S7 landmarks hidden (" + why + ")");
                _nextDiscoverMs = 0; // applied on the next frame
            }
            else RestoreAll(why);
        }

        // ============================================================================================ the statics reporter

 /// <summary>True when <paramref name="t"/> lies in a landmark hierarchy (hidden or not; walks its parents).</summary>
        public static bool Covers(Transform t)
        {
            if (Hiers.Count == 0) return false;
            for (; t != null; t = t.parent) if (Hiers.ContainsKey(t.gameObject.GetInstanceID())) return true;
            return false;
        }

        /// <summary>True when <paramref name="t"/> lies in a hierarchy hidden now.</summary>
        public static bool HiddenUnder(Transform t)
        {
            if (!Hidden || HiddenIds.Count == 0) return false;
            for (; t != null; t = t.parent) if (HiddenIds.Contains(t.gameObject.GetInstanceID())) return true;
            return false;
        }

        // ============================================================================================ C11

 /// <summary><c>de.landmarks {state: on | off | status}</c> (also <c>{landmarks: …}</c> or an argv word).</summary>
        public static bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            if (req == null || req.Cmd != Cmd) return false;
            Dictionary<string, object> a = S3Args.ToDict(req.Args);
            object v;
            if (!a.TryGetValue("state", out v) && !a.TryGetValue("landmarks", out v)) a.TryGetValue("action", out v);
            Command(v, reply.Str("cmd", req.Cmd));
            return true;
        }

        /// <summary>"on" shows the landmarks, "off" hides them, anything else (or null) answers the status.</summary>
        public static void Command(object v, JsonObjectWriter reply)
        {
            string s = v as string;
            bool b;
            if (s != null && s.Trim().ToLowerInvariant() == "status") s = null;
            if ((s != null || v is bool) && S3Args.TryBool(v, out b)) Set(!b, "C11 landmarks " + (b ? "on" : "off"));
            else if (v != null && s != null)
            {
                reply.Bool("ok", false).Str("error", "landmarks: on (shown) | off (hidden, the default) | status");
                return;
            }
            Status(reply.Bool("ok", !_off));
        }

        public static void Status(JsonObjectWriter w)
        {
            int r = 0, c = 0, l = 0, e = 0;
            var names = new List<string>();
            foreach (Hier h in Hiers.Values)
            {
                r += h.Renderers; c += h.Colliders; l += h.Lights; e += h.Emitters;
                if (names.Count < 8) names.Add(h.Name + (h.Applied ? " (hidden)" : ""));
            }
            w.Bool("landmarksHidden", Hidden).Num("landmarkHierarchies", (long)Hiers.Count).Str("landmarkNames", string.Join("; ", names.ToArray()))
             .Num("landmarkRenderersOff", (long)r).Num("landmarkCollidersOff", (long)c).Num("landmarkLightsOff", (long)l).Num("landmarkEmittersOff", (long)e)
             .Bool("exploderFound", CrashedShipExploder.main != null).Bool("radiationFound", LeakingRadiation.main != null)
             .Bool("warningsFound", _warnings != null).Num("landmarkVersion", (long)Version).Num("landmarkErrors", Errors)
             .Str("landmarksOff", _off ? _offWhy : "")
             .Str("line", "DE.S7 landmarks " + (Hidden ? "hidden" : "shown") + ": " + Hiers.Count + " hierarchies (" + string.Join("; ", names.ToArray()) + "), " +
                          r.ToString(CultureInfo.InvariantCulture) + " renderers, " + c.ToString(CultureInfo.InvariantCulture) + " colliders off");
        }
    }
}
