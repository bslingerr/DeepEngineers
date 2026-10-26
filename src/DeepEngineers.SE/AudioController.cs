using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>
 /// SE's audio mix (ARCHITECTURE §5.3, CONTRACT §1.1 "SE MyAudio.Static.VolumeGame/VolumeMusic/VolumeHud"),
 /// game-independent. The plugin Update (update thread) turns the view state machine's audio outputs into this
 /// update's decision (<see cref="Tick"/>); the <c>MySandboxGame.UpdateSound</c> postfix
 /// takes it once with <see cref="TakeForUpdateSound"/>
 /// and writes the volumes after SE's own focus logic of the same call. The Water Mod emitter scale is read by
 /// <c>WaterModAudio</c>.
 /// </summary>
    internal sealed class AudioController
    {
        private readonly object _lock = new object();
        private AudioDecision _decision;
        private bool _decisionFresh;
        private bool _restorePending;
        private bool _lastGate, _lastActive, _lastWrite, _logged;
        private volatile float _waterScale = 1f;

        public Action<string> Log = s => { };

        public AudioDecision Last { get { lock (_lock) return _decision; } }
        public float Mix { get { return Last.Mix; } }
        /// <summary>The Water Mod underwater-emitter multiplier (1 above water, 0 underwater; crossfaded).</summary>
        public float WaterScale { get { return _waterScale; } }
        public long PostfixRuns { get; private set; }
        public long PostfixWrites { get; private set; }
        /// <summary>The values the postfix wrote last (the options-screen Save prefix compares with them).</summary>
        public float WrittenGame { get; private set; }
        public float WrittenMusic { get; private set; }
        public bool HasWritten { get; private set; }
        public float ConfigGame { get; private set; }
        public float ConfigMusic { get; private set; }

        /// <summary>
 /// Plugin Update: this update's decision from the machine's outputs (or rest values when it did not step).
 /// <paramref name="restoreAllowed"/>: out of the world (the machine does not
 /// step, so its gate is closed), a mix the plugin left in SE's volumes (left the world underwater, through ESC
 /// and Exit, or a reload) is written back to the config values once SE may sound (focused, or audible
 /// unfocused; not loading or saving), so the main-menu music is not left at 0.
 /// </summary>
        public AudioDecision Tick(ref ViewOutputs o, bool stepped, bool bridgeUp, float configGame, float configMusic, bool restoreAllowed = false)
        {
            ConfigGame = configGame;
            ConfigMusic = configMusic;
            var d = new AudioDecision();
            float gameF = stepped ? o.SeGameFactor : 1f, musicF = stepped ? o.SeMusicFactor : 1f;
            d.Mix = stepped ? (float)o.MixLevel : 0f;
            d.MixActive = stepped && o.SeMixActive;
            d.GateOpen = stepped && o.SeAudioWrite;
            d.WaterScale = stepped ? AudioMath.Clamp01(o.WaterModFactor) : 1f;
            AudioMath.Volumes(configGame, configMusic, gameF, musicF, out d.Game, out d.Music, out d.Hud);
            d.Write = stepped ? AudioMath.ShouldWrite(d.GateOpen, bridgeUp, d.MixActive, ref _restorePending)
                              : AudioMath.ShouldRestoreOutsideWorld(restoreAllowed, ref _restorePending);
            if (!stepped && d.Write) Log("DE.Audio out of the world: SE volumes written back to the config (game " + F(d.Game) + " music " + F(d.Music) + ") after a mix");
            lock (_lock) { _decision = d; _decisionFresh = true; }
            _waterScale = d.WaterScale;
            if (!_logged || d.GateOpen != _lastGate || d.MixActive != _lastActive || d.Write != _lastWrite)
            {
                _logged = true;
                Log("DE.Audio gate=" + (d.GateOpen ? "open" : "closed") + " write=" + d.Write + " bridgeUp=" + bridgeUp + " mix=" + F(d.Mix) +
                    " seMixActive=" + d.MixActive + " game=" + F(d.Game) + " music=" + F(d.Music) + " hud=" + F(d.Hud) +
                    " waterMod=" + F(d.WaterScale) + " (config game " + F(configGame) + " music " + F(configMusic) + ")" +
                    (S2Mutants.VolumesWithoutFocusGate ? " MUTANT S2.M11: written without the gate" : ""));
                _lastGate = d.GateOpen;
                _lastActive = d.MixActive;
                _lastWrite = d.Write;
            }
            return d;
        }

        /// <summary>The UpdateSound postfix (any thread): this update's decision once; false when nothing is to be written.</summary>
        public bool TakeForUpdateSound(out AudioDecision d)
        {
            PostfixRuns++;
            lock (_lock)
            {
                d = _decision;
                if (!_decisionFresh) return false;
                _decisionFresh = false;
            }
            return d.Write;
        }

        /// <summary>Records what the postfix wrote.</summary>
        public void OnWritten(ref AudioDecision d)
        {
            PostfixWrites++;
            WrittenGame = d.Game;
            WrittenMusic = d.Music;
            HasWritten = true;
        }

        public Dictionary<string, object> Describe()
        {
            AudioDecision d = Last;
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "gateOpen", d.GateOpen },
                { "write", d.Write },
                { "mix", Math.Round(d.Mix, 4) },
                { "seMixActive", d.MixActive },
                { "game", Math.Round(d.Game, 4) },
                { "music", Math.Round(d.Music, 4) },
                { "hud", Math.Round(d.Hud, 4) },
                { "configGame", Math.Round(ConfigGame, 4) },
                { "configMusic", Math.Round(ConfigMusic, 4) },
                { "waterScale", Math.Round(d.WaterScale, 4) },
                { "postfixRuns", PostfixRuns },
                { "postfixWrites", PostfixWrites },
                { "restorePending", _restorePending },
                { "mutantS2M11", S2Mutants.VolumesWithoutFocusGate },
            };
        }

        private static string F(float v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
    }
}
