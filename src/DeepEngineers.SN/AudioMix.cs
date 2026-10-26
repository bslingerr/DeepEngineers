using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using FMOD;
using FMOD.Studio;
using FMODUnity;

namespace DeepEngineers.SN
{
    /// <summary>
 /// Subnautica's side of the audio mix (ROADMAP S2 SN item; ARCHITECTURE §5.3; CONTRACT §1 "Audio mix state"), FMOD
 /// 2.01.15 through FMODUnity:
 /// <list type="bullet">
 /// <item>Mute: <c>RuntimeManager.MuteAllEvents(bool)</c> (a <c>setMute</c> on <c>bus:/</c>; FreezeTime only writes
 /// that bus's volume and pause, never its mute; no game code calls MuteAllEvents, grep). The rule is
 /// <see cref="MixRules.ShouldMute"/>: no live SE, SE not foreground, quitting, or faded out to a target of 0 (above
 /// water).</item>
 /// <item>Level: the <c>bus:/</c> volume follows C5 <c>snBusTarget</c> through a linear <see cref="Crossfade"/> of C5
 /// <c>crossfadeMs</c> (300) on the unscaled clock. FreezeTime(FeedbackPanel) sets that volume to 0 on Begin and back
 /// to 1 on End (and IngameMenu/PDA write their own sub-buses), so the level and the mute are written again after
 /// </item>
 /// <item>The user's master volume is the game's master VCA (SoundSystem, persisted in the options): never touched.</item>
 /// <item>Seeded failure S2.M3: a C5 target of 0 (above water) is taken as 1, so Subnautica stays audible above water
 /// while unfrozen (Track).</item>
 /// </list>
 /// Until FMOD is initialised with banks loaded nothing is written (no RuntimeManager is created by us: its static
 /// Instance getter would create one).
 /// </summary>
    internal sealed class AudioMix
    {
        public const string MasterBus = "bus:/";

        public readonly Crossfade Fade = new Crossfade(0f);
        private Bus _bus;
        private bool _busOk, _dirty = true, _muteApplied, _waitLogged;
        private float _volApplied = -1f;
        private long _lastFades;

        public bool Muted { get { return _muteApplied; } }
        public string MuteWhy = "not applied yet";
        public float EffectiveTarget;
        public float CrossfadeMs = MixRules.DefaultCrossfadeMs;
        public long VolumeWrites, MuteWrites, Reapplies, ExternalMuteChanges;
        public bool Applied { get; private set; }

        /// <summary>A FreezeTime transition (or anything else that may have written bus:/): write volume and mute again.</summary>
        public void MarkDirty()
        {
            _dirty = true;
            Reapplies++;
        }

        /// <summary>One frame of the mix. Returns false while FMOD is not ready.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool Tick(ulong nowMs, bool valid, bool seForeground, bool quitting, float c5Target, float c5CrossfadeMs, bool aboveWaterMuteSkipped)
        {
            float eff = quitting ? 0f : MixRules.EffectiveTarget(valid, c5Target, aboveWaterMuteSkipped);
            float ms = MixRules.CrossfadeMs(c5CrossfadeMs);
            EffectiveTarget = eff;
            CrossfadeMs = ms;
            if (quitting) Fade.Set(0f, nowMs);
            float v = Fade.Step(eff, ms, nowMs);
            if (Fade.Fades != _lastFades)
            {
                _lastFades = Fade.Fades;
                DeLog.Info("DE.Audio fade " + F(v) + " -> " + F(eff) + " over " + F(ms) + " ms (C5 snBusTarget " + F(c5Target) +
                           (aboveWaterMuteSkipped && eff > 0f && MixRules.ClampTarget(c5Target) <= 0f ? ", MUTANT S2.M3: above-water mute skipped" : "") + ")");
            }
            string why;
            bool groups = S15SnMix.Active; // S15 hook (format audio.groups): the options VCAs carry the surface, so bus:/ stays at 1 and unmuted above water
            bool mute = MixRules.ShouldMute(valid, seForeground, quitting, groups ? 1f : v, groups ? 1f : eff, out why);
            AudioPolish.OnMix(nowMs, v, eff, c5Target, mute); // S7 audio polish (AudioPolish.cs): s7.audio rows, the A2 event-sound rule's bus target

            if (!RuntimeManager.IsInitialized || !RuntimeManager.HasBanksLoaded)
            {
                if (!_waitLogged) DeLog.Info("DE.Audio waiting for FMOD (RuntimeManager not initialised or banks not loaded); mix applied once ready");
                _waitLogged = true;
                _dirty = true;
                return false;
            }
            if (!_busOk || !_bus.isValid())
            {
                _busOk = RuntimeManager.StudioSystem.getBus(MasterBus, out _bus) == RESULT.OK && _bus.isValid();
                if (!_busOk) { _dirty = true; return false; }
                _dirty = true;
            }
            float busV = groups ? 1f : v;
            if (_dirty || Math.Abs(busV - _volApplied) > 1e-4f)
            {
                _bus.setVolume(busV);
                _volApplied = busV;
                VolumeWrites++;
            }

            // pauses bus:/ while Subnautica is frozen (its picture not shown: space, menus, death), so a line started then waited
 // and played when the freeze ended. While frozen the bus is kept running and muted instead: such sounds play out
 // silently and are never heard late. DEEPENGINEERS_SN_AUDIO_UNPAUSE=off: Subnautica's own pause.
            if (UnpauseWhenFrozen && UnityEngine.Time.timeScale == 0f)
            {
                bool busPaused;
                if (_bus.getPaused(out busPaused) == RESULT.OK && busPaused) { _bus.setPaused(false); Unpauses++; }
                if (!mute) { mute = true; why = "frozen: muted, not paused (no late sounds)"; }
            }
            if (_dirty || mute != _muteApplied || !Applied)
            {
                bool changed = !Applied || mute != _muteApplied;
                RuntimeManager.MuteAllEvents(mute);
                MuteWrites++;
                if (changed) DeLog.Info("DE.Audio " + (mute ? "MUTED" : "unmuted") + " (" + why + ") bus:/ volume " + F(v) + " target " + F(eff));
                _muteApplied = mute;
                MuteWhy = why;
            }
            S15SnMix.Tick(nowMs, v, eff, mute, why); // S15 hook: the effects at the S2 fade, the music and the voice live across the surface (never throws)
            _dirty = false;
            Applied = true;
            return true;
        }

        public static bool UnpauseWhenFrozen = !string.Equals(Environment.GetEnvironmentVariable("DEEPENGINEERS_SN_AUDIO_UNPAUSE"), "off", StringComparison.OrdinalIgnoreCase);
        public long Unpauses;

        /// <summary>Per second: the bus as FMOD reports it; an outside mute change makes the next frame write again.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public string Second()
        {
            if (!Applied || !_busOk || !_bus.isValid()) return "audio{" + (Applied ? "bus invalid" : "waiting for FMOD") + "}";
            float vol;
            bool m, p;
            _bus.getVolume(out vol);
            _bus.getMute(out m);
            _bus.getPaused(out p);
            if (m != _muteApplied)
            {
                ExternalMuteChanges++;
                _dirty = true;
            }
            return "audio{mute=" + (m ? "on" : "off") + "(" + MuteWhy + ") busVol=" + F(vol) + " paused=" + (p ? "yes" : "no") + " target=" + F(EffectiveTarget) +
                   " fade=" + F(Fade.Value) + (Fade.Fading ? "->" + F(Fade.Target) : "") + " writes=" + VolumeWrites + "/" + MuteWrites + " reapplies=" + Reapplies + "}";
        }

        private static string F(float v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
    }
}
