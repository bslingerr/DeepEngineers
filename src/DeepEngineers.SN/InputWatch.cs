using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DeepEngineers.SN
{
    /// <summary>
 /// Input never reaches Subnautica (ROADMAP S2 SN item "input silencing (from S1) plus logging of the Input System
 /// device state"; ARCHITECTURE §5.2; oracle S2.T-I1 "Subnautica GameInput all zero; Input System devices disabled or
 /// zero"). The silencer itself is S1's (<c>GameInput.ClearInput(1)</c> every frame in S1Host and GhostPlayer, a dummy
 /// handler on InputHandlerStack). This class measures, once per second, what is left:
 /// <list type="bullet">
 /// <item>Input System (package 1.4.4, VERIFIED probe-display-input §2.2): <c>InputSystem.settings.backgroundBehavior</c>
 /// (default ResetAndDisableNonBackgroundDevices; GameInputSystem never sets it, grep), every device with
 /// <c>enabled</c> and <c>canRunInBackground</c>, and per frame the raw mouse delta and keyboard any-key of the current
 /// devices (what Subnautica would receive without the silencer).</item>
 /// <item>GameInput as the game reads it: <c>GetLookDelta</c>, <c>GetMoveDirection</c> and <c>GetButtonDown</c> of the
 /// gameplay buttons (0 while <c>GameInput.clearInput</c>).</item>
 /// </list>
 /// One line `DE.Input sec …` per second (sums over the second, then reset). Seeded failure S2.M2: the ClearInput
 /// silencer off (<see cref="S2Patches"/>) and <c>backgroundBehavior = IgnoreFocus</c> (the original value restored when
 /// the mutant goes off and at disposal).
 /// </summary>
    internal sealed class InputWatch
    {
        private static readonly GameInput.Button[] Buttons =
        {
            GameInput.Button.Jump, GameInput.Button.PDA, GameInput.Button.Exit, GameInput.Button.LeftHand, GameInput.Button.RightHand,
            GameInput.Button.Slot1, GameInput.Button.Slot2, GameInput.Button.Slot3, GameInput.Button.Slot4, GameInput.Button.Slot5,
            GameInput.Button.Sprint, GameInput.Button.MoveUp, GameInput.Button.MoveDown, GameInput.Button.MoveForward,
            GameInput.Button.MoveBackward, GameInput.Button.MoveLeft, GameInput.Button.MoveRight, GameInput.Button.Reload,
            GameInput.Button.AltTool, GameInput.Button.CycleNext, GameInput.Button.CyclePrev,
        };

        private bool _bgSaved, _bgForced;
        // kept as an int: no field of an Input System type, so this class loads even if that assembly changed shape
        private int _bgOriginal;

        // sums over the current second
        private double _rawMouse, _gameLook, _gameMove;
        private int _rawKeyFrames, _gameButtons, _frames, _clearedFrames;

        public long TotalRawFrames, TotalGameFrames;

        /// <summary>Per frame (in world).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Frame(bool silencerMutant)
        {
            if (silencerMutant && !_bgForced) ForceIgnoreFocus();
            else if (!silencerMutant && _bgForced) RestoreBackground("S2.M2 off");

            _frames++;
            bool raw = false, game = false;
            Mouse m = Mouse.current;
            if (m != null && m.enabled)
            {
                float d = m.delta.ReadValue().magnitude;
                if (d > 0f) { _rawMouse += d; raw = true; }
            }
            Keyboard k = Keyboard.current;
            if (k != null && k.enabled && k.anyKey.isPressed) { _rawKeyFrames++; raw = true; }

            if (GameInput.clearInput) _clearedFrames++;
            float look = GameInput.GetLookDelta().magnitude;
            float move = GameInput.GetMoveDirection().magnitude;
            if (look > 0f) { _gameLook += look; game = true; }
            if (move > 0f) { _gameMove += move; game = true; }
            for (int i = 0; i < Buttons.Length; i++)
                if (GameInput.GetButtonDown(Buttons[i])) { _gameButtons++; game = true; }
            if (raw) TotalRawFrames++;
            if (game) TotalGameFrames++;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ForceIgnoreFocus()
        {
            InputSettings s = InputSystem.settings;
            if (s == null) return;
            if (!_bgSaved) { _bgSaved = true; _bgOriginal = (int)s.backgroundBehavior; }
            s.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            _bgForced = true;
            DeLog.Warn("DE.Input backgroundBehavior " + (InputSettings.BackgroundBehavior)_bgOriginal + " -> IgnoreFocus and the ClearInput silencer off (MUTANT S2.M2)");
        }

        /// <summary>The original backgroundBehavior back (mutant off, bridge disposal).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void RestoreBackground(string why)
        {
            if (!_bgForced) return;
            _bgForced = false;
            try
            {
                InputSettings s = InputSystem.settings;
                if (s != null && _bgSaved) s.backgroundBehavior = (InputSettings.BackgroundBehavior)_bgOriginal;
                DeLog.Info("DE.Input backgroundBehavior restored to " + (InputSettings.BackgroundBehavior)_bgOriginal + " (" + why + ")");
            }
            catch (Exception e)
            {
                DeLog.Warn("DE.Input backgroundBehavior restore failed (" + why + "): " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>The `DE.Input sec` line; the sums restart.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public string Second()
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(320);
            sb.Append("DE.Input sec focused=").Append(Application.isFocused ? "true" : "false");
            InputSettings s = InputSystem.settings;
            sb.Append(" backgroundBehavior=").Append(s != null ? s.backgroundBehavior.ToString() : "n/a");
            sb.Append(" devices=[");
            int n = 0, enabled = 0;
            foreach (InputDevice d in InputSystem.devices)
            {
                if (d == null) continue;
                if (n > 0) sb.Append(',');
                sb.Append(d.name).Append(':').Append(d.enabled ? "on" : "off");
                if (d.canRunInBackground) sb.Append("+bg");
                n++;
                if (d.enabled) enabled++;
            }
            sb.Append("] enabled=").Append(enabled.ToString(ci)).Append('/').Append(n.ToString(ci));
            sb.Append(" raw{mouseDelta=").Append(_rawMouse.ToString("0.###", ci)).Append(" keyFrames=").Append(_rawKeyFrames.ToString(ci)).Append('}');
            sb.Append(" game{look=").Append(_gameLook.ToString("0.###", ci)).Append(" move=").Append(_gameMove.ToString("0.###", ci))
              .Append(" buttonsDown=").Append(_gameButtons.ToString(ci)).Append(" clearedFrames=").Append(_clearedFrames.ToString(ci))
              .Append('/').Append(_frames.ToString(ci)).Append('}');
            sb.Append(" silencer=").Append(S2Patches.SilencerMutant ? "OFF(MUTANT S2.M2)" : "on");
            sb.Append(" clearInputSkipped=").Append(S2Patches.ClearInputSkipped.ToString(ci));
            sb.Append(" totals{rawFrames=").Append(TotalRawFrames.ToString(ci)).Append(" gameFrames=").Append(TotalGameFrames.ToString(ci)).Append('}');
            _rawMouse = _gameLook = _gameMove = 0;
            _rawKeyFrames = _gameButtons = _frames = _clearedFrames = 0;
            return sb.ToString();
        }
    }
}
