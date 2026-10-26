using System;
using System.Globalization;
using DeepEngineers.Wire;
using FMOD;
using FMOD.Studio;
using FMODUnity;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S15, Subnautica's audio across the surface (format audio.groups
 /// when going above the water", and a voice line under way cut the same way). The S2 rule (AudioMix: one game's audio at a
 /// time) faded <c>bus:/</c> to C5 snBusTarget and muted it above water. With the groups on, AudioMix keeps <c>bus:/</c> at 1
 /// and unmuted above water (S15 hook in AudioMix.Tick; still muted with no live SE, SE not in front, quitting) and this
 /// class carries the surface on Subnautica's own options VCAs
 /// <list type="bullet">
 /// <item>the effects and ambience VCA = the player's slider value x the S2 rule's own fade (0 above water, as before);</item>
 /// <item>the music and voice VCAs = the player's value x their group level: 1 under water, Mix.MusicAbovePermille /
 /// Mix.VoiceAbovePermille above, over Mix.CrossfadeMs (<see cref="S15SnGroups"/>), so the music plays on and a line started
 /// under water ends above it.</item>
 /// </list>
 /// The player's value is tracked by read-back: a VCA volume other than the one this class wrote last was SoundSystem's (an
 /// options change) and becomes the new factor. FMOD Studio API: <c>System.getVCAByID</c>
 /// <c>VCA.getVolume/setVolume</c>. Without the three VCAs, with
 /// DEEPENGINEERS_S15 "nomix" or under S15.M19 the S2 rule's old bus:/ fade and mute carry the surface (and every VCA this
 /// class wrote is given back). Main thread (AudioMix.Tick). Never throws.
 /// </summary>
    internal static class S15SnMix
    {
        public static bool On = true;
        /// <summary>The groups carry the surface (AudioMix keeps bus:/ at 1 and skips the above-water mute); read by AudioMix's hook, set here.</summary>
        public static bool Active { get; private set; }
        public static readonly S15SnGroups Groups = new S15SnGroups();
        /// <summary>The last frame's levels (the voice line watch reads them): effects, music, voice; the S2 target; the master mute.</summary>
        public static float LastSfx = 1f, LastMusic = 1f, LastVoice = 1f, LastS2 = 1f;
        public static bool LastMuted;
        /// <summary>AudioMix's reason for the master mute (MixRules.ShouldMute: no live SE, SE not foreground, quitting, audible).</summary>
        public static string LastMuteWhy = "";
        public static long Writes, Restores, ExternalChanges, Rows;
        public static string Why = "not started";

        private static readonly Guid SfxId = new Guid(S15Conventions.Mix.SfxVca), MusicId = new Guid(S15Conventions.Mix.MusicVca), VoiceId = new Guid(S15Conventions.Mix.VoiceVca);
        private static VCA _sfx, _music, _voice;
        private static bool _resolved, _resolveLogged, _wrote, _configured;
        private static float _uSfx = -1f, _uMusic = -1f, _uVoice = -1f, _wSfx = -1f, _wMusic = -1f, _wVoice = -1f;
        private static ulong _nextRowMs, _nextResolveMs;
        private static string _rowKey = "";

        private static void Configure()
        {
            if (_configured) return;
            _configured = true;
            string v = null;
            try { v = Environment.GetEnvironmentVariable(S15Sn.EnvVar); } catch (Exception) { }
            v = v != null ? v.Trim().ToLowerInvariant() : null;
            if (v == "off" || v == "0" || v == "false" || (v != null && v.Contains("nomix"))) On = false;
            DeLog.Info("DE.S15 audio groups " + (On ? "on" : "OFF (" + S15Sn.EnvVar + "=" + v + ")") + ": music " + Groups.MusicAbove.ToString("0.##", CultureInfo.InvariantCulture) +
                       " and voice " + Groups.VoiceAbove.ToString("0.##", CultureInfo.InvariantCulture) + " above water, crossfade " + S15Conventions.Mix.CrossfadeMs + " ms");
        }

        /// <summary>
 /// AudioMix.Tick's S15 hook, once FMOD is ready, every frame: <paramref name="sfxLevel"/> = the S2 rule's fade value (its
 /// bus:/ level of old), <paramref name="s2Target"/> = its effective target, <paramref name="muted"/> = the master mute and
 /// <paramref name="muteWhy"/> its reason (the s15.mix row carries it: S2's own mutes, no live SE, SE not in front and
 /// quitting, stay; only the above-water one is gone with the groups).
 /// </summary>
        public static void Tick(ulong nowMs, float sfxLevel, float s2Target, bool muted, string muteWhy)
        {
            LastMuteWhy = muteWhy ?? "";
            try { TickCore(nowMs, sfxLevel, s2Target, muted); }
            catch (Exception e)
            {
                Active = false;
                Why = "error " + e.GetType().Name;
                DeLog.Warn("DE.Error S15 audio groups " + e.GetType().Name + ": " + e.Message + " (the S2 rule's bus:/ fade carries the surface)");
            }
        }

        private static void TickCore(ulong nowMs, float sfxLevel, float s2Target, bool muted)
        {
            Configure();
            LastMuted = muted;
            LastS2 = s2Target;
            bool mutant = DevMutants.IsOn(S15SnMutants.MusicCut);
            if (!_resolved && nowMs >= _nextResolveMs) Resolve(nowMs);
            string why = !On ? "off (" + S15Sn.EnvVar + ")" : mutant ? "S15.M19 (the old cut)" : !_resolved ? "the options VCAs not found" : null;
            if (why != null)
            {
                if (_wrote) Restore();
                Active = false;
                Why = why;
                // the old rule: everything at the S2 fade (the bus:/ level AudioMix writes)
                LastSfx = LastMusic = LastVoice = sfxLevel;
                Groups.Reset(nowMs);
                Row(nowMs, sfxLevel, s2Target, muted, mutant);
                return;
            }
            Active = true;
            Why = "groups";
            float music, voice;
            Groups.Step(nowMs, s2Target, out music, out voice);
            Apply(_sfx, ref _uSfx, ref _wSfx, sfxLevel);
            Apply(_music, ref _uMusic, ref _wMusic, music);
            Apply(_voice, ref _uVoice, ref _wVoice, voice);
            LastSfx = sfxLevel;
            LastMusic = music;
            LastVoice = voice;
            Row(nowMs, sfxLevel, s2Target, muted, mutant);
        }

        private static void Resolve(ulong nowMs)
        {
            _nextResolveMs = nowMs + 5000;
            FMOD.Studio.System sys = RuntimeManager.StudioSystem;
            bool ok = sys.getVCAByID(SfxId, out _sfx) == RESULT.OK && _sfx.isValid();
            ok &= sys.getVCAByID(MusicId, out _music) == RESULT.OK && _music.isValid();
            ok &= sys.getVCAByID(VoiceId, out _voice) == RESULT.OK && _voice.isValid();
            _resolved = ok;
            if (_resolveLogged && !ok) return;
            _resolveLogged = true;
            DeLog.Info(ok ? "DE.S15 audio groups: the options VCAs found (" + S15Conventions.Mix.VcaSource + "): the music and the voice stay live above water"
                          : "DE.S15 audio groups: the options VCAs " + S15Conventions.Mix.VcaSource + " not found (retried every 5 s): the S2 rule's old bus:/ fade carries the surface");
        }

        /// <summary>One VCA: the player's value (read back when SoundSystem changed it) x the level.</summary>
        private static void Apply(VCA vca, ref float user, ref float written, float level)
        {
            if (!vca.isValid()) return;
            float now;
            if (vca.getVolume(out now) != RESULT.OK) return;
            if (user < 0f || Math.Abs(now - written) > 1e-4f)
            {
                if (user >= 0f) ExternalChanges++;
                user = now;   // SoundSystem's value (the player's options slider): the factor
            }
            float want = (float)S15MixRules.VcaVolume(user, level);
            if (Math.Abs(want - written) <= 1e-4f && Math.Abs(now - written) <= 1e-4f) return;
            vca.setVolume(want);
            written = want;
            _wrote = true;
            Writes++;
        }

        /// <summary>Every VCA this class wrote gets the player's own value back (the groups off, the world left).</summary>
        public static void Restore()
        {
            try
            {
                if (_sfx.isValid() && _uSfx >= 0f) _sfx.setVolume(_uSfx);
                if (_music.isValid() && _uMusic >= 0f) _music.setVolume(_uMusic);
                if (_voice.isValid() && _uVoice >= 0f) _voice.setVolume(_uVoice);
            }
            catch (Exception) { }
            _wrote = false;
            _wSfx = _wMusic = _wVoice = -1f;
            _uSfx = _uMusic = _uVoice = -1f;
            Restores++;
            DeLog.Info("DE.S15 audio groups: the player's VCA volumes given back");
        }

        private static float Read(VCA vca)
        {
            float v;
            return vca.isValid() && vca.getVolume(out v) == RESULT.OK ? v : -1f;
        }

        /// <summary>s15.mix: at each change of the state, every RowMs while a group fades, else every 5 s.</summary>
        private static void Row(ulong nowMs, float sfx, float s2Target, bool muted, bool mutant)
        {
            string key = (Active ? "A" : "a") + (muted ? "M" + LastMuteWhy : "m") + (s2Target >= 0.5f ? "U" : "u") + Groups.MusicAbove.ToString("0.###", CultureInfo.InvariantCulture) +
                         "/" + Groups.VoiceAbove.ToString("0.###", CultureInfo.InvariantCulture) + Why;
            bool fading = Groups.Fading || (sfx > 0f && sfx < 1f);
            if (key == _rowKey && nowMs < _nextRowMs) return;
            _rowKey = key;
            _nextRowMs = nowMs + (ulong)(fading ? S15Conventions.Mix.RowMs : 5000);
            Rows++;
            JsonOut j = S5Rows.Begin("s15.mix");
            j.Bool("active", Active).Str("why", Why).Num("sfx", (double)LastSfx).Num("music", (double)LastMusic).Num("voice", (double)LastVoice)
             .Num("musicTarget", (double)Groups.MusicTarget).Num("voiceTarget", (double)Groups.VoiceTarget).Num("musicAbove", (double)Groups.MusicAbove)
             .Num("voiceAbove", (double)Groups.VoiceAbove).Num("s2Target", (double)s2Target).Bool("muted", muted).Str("muteWhy", LastMuteWhy).Num("ms", (double)nowMs)
             .Num("userSfx", (double)_uSfx).Num("userMusic", (double)_uMusic).Num("userVoice", (double)_uVoice)
             .Num("vcaSfx", (double)Read(_sfx)).Num("vcaMusic", (double)Read(_music)).Num("vcaVoice", (double)Read(_voice))
             .Num("crossfadeMs", (double)S15Conventions.Mix.CrossfadeMs).Bool("mutant", mutant);
            S5Rows.End(j);
        }

        public static string Status()
        {
            return "audio groups " + (Active ? "on" : "off (" + Why + ")") + ": sfx " + LastSfx.ToString("0.00", CultureInfo.InvariantCulture) + " music " +
                   LastMusic.ToString("0.00", CultureInfo.InvariantCulture) + " voice " + LastVoice.ToString("0.00", CultureInfo.InvariantCulture) + " (above water: music " +
                   Groups.MusicAbove.ToString("0.##", CultureInfo.InvariantCulture) + ", voice " + Groups.VoiceAbove.ToString("0.##", CultureInfo.InvariantCulture) + "), writes " + Writes;
        }
    }
}
