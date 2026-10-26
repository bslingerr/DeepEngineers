using System;
using System.Globalization;
using DeepEngineers.Wire;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S16 depth progression, the Subnautica side (ROADMAP §14 "### S16" point 3; CONTRACT §5.4 "S16 conventions"): the
 /// hull creaks, the Cyclops "Hull integrity critical" voice and the crush-damage groan, played from Subnautica's own FMOD
 /// events when SE counts the events in the C5 extension (one sound per increment, <see cref="S16SnEvents"/>). Main
 /// thread, once per frame from Bridge.Update (S16 hook), only in the world with SE alive and a fresh C5. Nothing is
 /// shipped and nothing in the game is changed: the sounds are the game's own assets, read from its prefabs once per
 /// world, all VERIFIED
 /// <list type="bullet">
 /// <item>creaks: <c>BaseHullStrength.crushSounds</c>, the three graded crush
 /// creaks a base plays when its hull strength is negative (indices 0..2 by severity), from
 /// <c>BaseGhost.GetBasePrefab</c>;
 /// level 1/2/3 → index 0/1/2;</item>
 /// <item>the voice: <c>SubRoot.hullCriticalNotification</c> of the Cyclops, its
 /// <c>sound</c>, played directly: <c>VoiceNotification.Play</c> is suppressed by
 /// S1's UI prefixes and uses the game clock and the prefab's position;</item>
 /// <item>the groan on damage: the Cyclops's <c>CrushDamage.soundOnDamage</c>
 /// the sound Subnautica plays each crush tick.</item>
 /// </list>
 /// The Cyclops prefab comes from <c>LightmappedPrefabs.main.GetScenePrefab("cyclops")</c>
 /// a missing source logs
 /// <c>DE.S16 MISSING &lt;what&gt;</c> once and falls back (creaks ↔ groan) or stays silent. Each sound is
 /// <c>FMODUWE.PlayOneShot(FMODAsset, Vector3, float)</c> at the camera
 /// so it plays inside the hull, through Subnautica's mix (S2 sets its bus by the view state).
 /// Subnautica's own depth meter (<c>uGUI_DepthCompass</c>) is not driven: the plugin masks Subnautica's HUD in the
 /// bridge (<c>GUIController.SetHidePhase(MaskHUD)</c>, ARCHITECTURE §5.11); the depth, crush depth and warnings are SE's
 /// HUD line (S16Host on SE).
 /// </summary>
    internal static class S16Sn
    {
        private static readonly S16SnEvents Events = new S16SnEvents();
        private static FMODAsset[] _creaks;
        private static FMODAsset _voice, _groan;
        private static bool _resolved, _loggedSources;
        private static float _nextResolveS, _giveUpS = -1f;
        private static bool _inWorldLast;
        public static long Creaks, Voices, Groans, Silent, Errors, HudErrors;
        private static float _nextErrorS, _nextHudErrorS;
        /// <summary>Time.unscaledTime of the last voice played (format voice.mix: S16's own sounds ducked after it), -1 = none.</summary>
        private static float _lastVoiceS = -1f;
        public static long Ducked;

        /// <summary>Bridge hook: once per frame. Never throws.</summary>
        public static void Frame(ulong nowMs, bool inWorld, bool seAlive, bool seStateValid, ref SeState st)
        {
            try
            {
                if (!inWorld)
                {
                    if (_inWorldLast)
                    {
                        Events.Reset(); _resolved = false; _creaks = null; _voice = _groan = null; _loggedSources = false; _giveUpS = -1f; _lastVoiceS = -1f;
                        S16SnHud.Teardown("world gone");
                    }
                    _inWorldLast = false;
                    return;
                }
                _inWorldLast = true;
                S16WireState w = default(S16WireState);
                bool have = seAlive && seStateValid && S16Wire.TryRead(ref st, out w);
                float now = Time.unscaledTime;
                // HUD A/B: Subnautica's own depth meter and vehicle readouts while the picture is Subnautica's (format hud.sn)
                try { S16SnHud.Frame(S16Hud.SnHudMode(have, w.Flags, w.HudState, st.viewState), ref w, now); }
                catch (Exception e)
                {
                    HudErrors++;
                    if (now >= _nextHudErrorS) { _nextHudErrorS = now + 10f; DeLog.Warn("DE.Error S16 HUD " + e.GetType().Name + ": " + e.Message); }
                }
                S16SnPlay p = Events.Step(have, ref w, now, DevMutants.IsOn(S16SnMutants.CreakClock));
                S15SnVoiceClock.HoldS16(ref p.Voice, now); // S15 hook: one voice at a time both ways: the hull voice waits out an S15 line's gap (held, not dropped)
                if (!p.Any) return;
                if (!_resolved) Resolve(now);
                Camera cam = MainCamera.camera;
                Vector3 at = cam != null ? cam.transform.position : Vector3.zero;
                string src;
                // format voice.
 // then S16's own creak and groan of the same frame and the next
 // DuckMs ducked under it. Vanilla plays the Cyclops voice the same way
 // so the event keeps its own mixer routing (its voice bus).
                if (p.Voice && Play("voice", _voice, _voice != null ? "SubRoot.hullCriticalNotification.sound (Cyclops)" : "none", at, S16VoiceMix.VoiceVolume, p.VoiceSeq, p.CreakLevel, ref Voices))
                    _lastVoiceS = now;
                double sinceVoiceMs = S15SnVoiceClock.SinceVoiceMs(now, _lastVoiceS); // S15 hook: S16's creaks and groans duck under S15's lines too
                if (p.Creak) { FMODAsset a = CreakAsset(p.CreakLevel, out src); Play("creak", a, src, at, Duck(S16SnEvents.CreakVolume(p.CreakLevel), sinceVoiceMs), p.CreakSeq, p.CreakLevel, ref Creaks); }
                if (p.Damage)
                {
                    FMODAsset a = _groan;
                    src = "CrushDamage.soundOnDamage.asset (Cyclops)";
                    if (a == null) a = CreakAsset(3, out src);
                    Play("damage", a, src, at, Duck(1f, sinceVoiceMs), p.DamageSeq, p.CreakLevel, ref Groans);
                }
            }
            catch (Exception e)
            {
                Errors++;
                float t = Time.unscaledTime;
                if (t >= _nextErrorS) { _nextErrorS = t + 10f; DeLog.Warn("DE.Error S16 frame " + e.GetType().Name + ": " + e.Message); }
            }
        }

        private static FMODAsset CreakAsset(int level, out string source)
        {
            int i = Math.Max(1, Math.Min(3, level)) - 1;
            if (_creaks != null && _creaks.Length > i && _creaks[i] != null) { source = "BaseHullStrength.crushSounds[" + i + "]"; return _creaks[i]; }
            source = _groan != null ? "CrushDamage.soundOnDamage.asset (Cyclops; fallback)" : "none";
            return _groan;
        }

        /// <summary>Bridge hook (WriteStatus): the C7 S16 block (format c7.s16), every frame.</summary>
        public static void FillStatus(ref SnStatus s)
        {
            try { S16Wire.WriteStatus(ref s, (byte)S16SnHud.Flags); }
            catch (Exception) { }
        }

        private static float Duck(float volume, double sinceVoiceMs)
        {
            float v = S16VoiceMix.EffectVolume(volume, sinceVoiceMs);
            if (v < volume) Ducked++;
            return v;
        }

        private static bool Play(string kind, FMODAsset a, string source, Vector3 at, float volume, uint seq, int level, ref long counter)
        {
            bool played = false;
            if (a != null)
            {
                FMODUWE.PlayOneShot(a, at, volume);
                played = true;
                counter++;
            }
            else Silent++;
            JsonOut j = S5Rows.Begin("s16." + kind);
            j.Num("seq", (long)seq).Num("level", (long)level).Bool("played", played).Str("source", source).Str("path", a != null ? a.path ?? "?" : "missing")
             .Num("volume", (double)volume).Num("coalesced", Events.Coalesced).Bool("mutant", DevMutants.IsOn(S16SnMutants.CreakClock));
            S5Rows.End(j);
            //  one explicit line per sound (the S16b session greps them): what, which SE event, played or not, the source and its FMOD path
            DeLog.Info("DE.S16 sound " + kind + " seq=" + seq.ToString(CultureInfo.InvariantCulture) + " level=" + level.ToString(CultureInfo.InvariantCulture) +
                       " played=" + (played ? "1" : "0") + " volume=" + volume.ToString("0.00", CultureInfo.InvariantCulture) + " source=" + source + " path=" + (a != null ? a.path ?? "?" : "-") +
                       " coalesced=" + Events.Coalesced.ToString(CultureInfo.InvariantCulture));
            return played;
        }

        /// <summary>The game's own assets, read once per world (retried every 5 s for 60 s while a source is not loaded yet).</summary>
        private static void Resolve(float now)
        {
            if (now < _nextResolveS) return;
            _nextResolveS = now + 5f;
            if (_giveUpS < 0f) _giveUpS = now + 60f;
            if (_creaks == null)
            {
                GameObject basePrefab = BaseGhost.GetBasePrefab();
                BaseHullStrength bhs = basePrefab != null ? basePrefab.GetComponentInChildren<BaseHullStrength>(true) : null;
                if (bhs != null && bhs.crushSounds != null && bhs.crushSounds.Length > 0) _creaks = bhs.crushSounds;
            }
            if (_voice == null || _groan == null)
            {
                GameObject cyclops = LightmappedPrefabs.main != null ? LightmappedPrefabs.main.GetScenePrefab("cyclops") : null;
                if (cyclops != null)
                {
                    SubRoot sub = cyclops.GetComponentInChildren<SubRoot>(true);
                    if (_voice == null && sub != null && sub.hullCriticalNotification != null) _voice = sub.hullCriticalNotification.sound;
                    CrushDamage cd = cyclops.GetComponentInChildren<CrushDamage>(true);
                    if (_groan == null && cd != null && cd.soundOnDamage != null) _groan = cd.soundOnDamage.asset;
                }
            }
            bool all = _creaks != null && _voice != null && _groan != null;
            if (all || now >= _giveUpS)
            {
                _resolved = true;
                if (!_loggedSources)
                {
                    _loggedSources = true;
                    if (_creaks != null)
                        for (int i = 0; i < _creaks.Length; i++) DeLog.Info("DE.S16 sound creak" + (i + 1) + " = BaseHullStrength.crushSounds[" + i + "] path=" + (_creaks[i] != null ? _creaks[i].path : "null"));
                    else DeLog.Warn("DE.S16 MISSING BaseHullStrength.crushSounds (BaseGhost.GetBasePrefab() not loaded): creaks fall back to the Cyclops groan");
                    if (_voice != null) DeLog.Info("DE.S16 sound voice = SubRoot.hullCriticalNotification.sound path=" + _voice.path);
                    else DeLog.Warn("DE.S16 MISSING SubRoot.hullCriticalNotification (the cyclops prefab is not loaded): no voice");
                    if (_groan != null) DeLog.Info("DE.S16 sound damage = CrushDamage.soundOnDamage.asset (Cyclops) path=" + _groan.path);
                    else DeLog.Warn("DE.S16 MISSING Cyclops CrushDamage.soundOnDamage: the damage groan falls back to the strongest creak");
                }
            }
        }

        public static string Status()
        {
            return "creaks " + Creaks + ", voices " + Voices + ", groans " + Groans + ", silent " + Silent + ", events " + Events.CreakEvents + "/" + Events.VoiceEvents + "/" +
                   Events.DamageEvents + ", coalesced " + Events.Coalesced + ", sources creak " + (_creaks != null) + " voice " + (_voice != null) + " groan " + (_groan != null) +
                   ", HUD " + S16SnHud.Mode + " flags " + S16SnHud.Flags;
        }
    }
}
