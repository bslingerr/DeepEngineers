using System;
using System.Globalization;
using System.Text;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S14 "Audio v2" (ROADMAP §14 "### S14"; docs/S14-BRIEF.
 // point 5 (the thrusters' look above the water). Nothing new travels on the wire (layout 1.0 and every earlier pin
 // unchanged): what both plugins and the analyzer twin must agree on is the rule set below, pinned on its own:
 // - point 1, the jetpack loop: SE's MyCharacterSoundComponent.SelectSound answers its own EmptySoundPair while the S2b
 // swim latch holds the character in the Flying state with the jetpack off
 // - point 2, one underwater treatment for every modifiable SE sound: SE's own effect (LowPassHelmet, 1800 Hz,
 // Content/Data/AudioEffects.sbc
 // in a
 // Realistic world SE's Real* recordings are swapped for the Arcade ones under water, so both modes play one low-pass;
 // - point 3, ship weapons under water: the Water Mod's own per-mod config (Data/WaterConfig.xml BlockConfigs) in the
 // world mod, FunctionalUnderwater true for its 20 weapon entries;
 // - point 4, thrusters: Subnautica's FMOD listener on the camera, the engine loop's envelope and level curve, SE's own
 // thruster sound faded out under water (Subnautica's engine loop is the under-water version, SE's above);
 // - point 5, thrusters above the water: Subnautica's own Prawn jet effect per above-water flame, scaled by thrust, as long
 // as SE's flame, in place of scene-above's thruster puffs;
 // - round 4, the left ear: SE's 3D pan of a voice whose channels are 2 (or not the count SE used) is rebuilt from one valid
 // 1-channel X3DAudio calculation (SE's own call passes a 2-channel DSP with a 1-channel emitter, outside X3DAudio's
 // contract); every 3D voice's pan is measured against the geometry (s14.pan).
 // The canonical text below is pinned (FNV-1a 64; a changed number changes the hash and turns S14ConventionsTests red);
 // the Python twin is tests/s14/analyze_s14.py (same text, same hash, same envelope, checked by its --selftest).
 // =================================================================================================================

    public static class S14Conventions
    {
        public const string Header = "ext s14 audio 6";
        /// <summary>FNV-1a 64 of <see cref="CanonicalText"/> as pinned in CONTRACT §5.4 "S14 conventions".</summary>
        public const ulong PinnedHash = 0x1261b20244b0b097UL;
        public const int CanonicalLines = 70, CanonicalBytes = 7180;

        /// <summary>Point 2: SE's own effect for every modifiable SE sound under water (AudioEffects.sbc: LowPass 1800 Hz; round 4,
 /// <c>/de s7audio effect LowPassCockpit</c> brings it back live).</summary>
        public const string UnderwaterEffect = "LowPassHelmet";

        /// <summary>Point 2: the effect's cutoff, and the readback threshold above which a voice counts as unfiltered (XAudio2's open filter reads 7,350 Hz).</summary>
        public static class Effect
        {
            public const int UnderwaterCutoffHz = 1800;
            public const int DryCutoffMinHz = 5000;
            /// <summary>SE's conversion of a definition's Hz into XAudio2's filter frequency.</summary>
            public const int SampleRateHz = 44100;
        }

        /// <summary>Point 4: the engine loop's envelope and level curve; SE's thruster fade.</summary>
 /// <summary>

 /// the eye's own submersion, S2's camera SN y below EyeYMm (mm), not the view machine's mix (its waterline hysteresis, its
 /// dwell, its 300 ms ramp); the s14.eye counts judge it outside DeadbandMm of the surface (one frame of lag at a crossing).
 /// </summary>
        public static class Muffle
        {
            public const int EyeYMm = 0;
            public const int DeadbandMm = 50;
        }

        public static class Envelope
        {
            public const int AttackMs = 200;
            public const int ReleaseMs = 300;
            /// <summary>The engine loop's level at rpm 0 (small corrections), per mille of full.</summary>
            public const int GainMinPermille = 250;
            /// <summary>gain = min + (1 - min) rpm^(this / 1000).</summary>
            public const int GainExponentPermille = 1500;
            public const int SeThrusterFadeMs = 300;
        }

        /// <summary>Point 4a: the listener readback tolerance against the camera.</summary>
        public static class Listener
        {
            public const int ToleranceMm = 50;
            public const int ToleranceCentiDeg = 200;
        }

        /// <summary>Point 1: rows and the analyzer's grace after the jetpack went off.</summary>
        public static class Jetpack
        {
            public const int GraceMs = 500;
        }

        public static class Rows
        {
            public const int PeriodMs = 1000;
        }

        /// <summary>Round 4 (the left ear): SE's 3D pan rebuilt for 2-channel voices, and the pan judged against the geometry.</summary>
        public static class Pan
        {
            /// <summary>A voice is one-sided when its quieter side is below this share of its louder side.</summary>
            public const int OneSidedPermille = 20;
            /// <summary>|geometric pan| below this: the voice is roughly in front or behind (about 20 deg), never one-sided.</summary>
            public const int CentredPermille = 350;
            /// <summary>|geometric pan| from this: the matrix pan has the geometry's sign.</summary>
            public const int SignMinPermille = 300;
            /// <summary>Each source channel of a 2-channel voice in 3D (a mono point: same loudness as a 1-channel voice of identical channels).</summary>
            public const int DownmixPermille = 500;
        }

        /// <summary>Round 4 (too muffled in a Realistic world): the low-pass treatments a modifiable SE voice carries under water.</summary>
        public static class Chain
        {
            public const int MaxLowPass = 1;
        }

        /// <summary>Point 3: the Water Mod 3.23 weapon entries the world mod overrides (Water Mod Data/WaterConfig.xml lines 38-57).</summary>
        public static class Weapons
        {
            public const int Configs = 20;
        }

        /// <summary>

 /// thruster type plays under water. <c>engine se</c> (default): SE's own cue per thruster definition; <c>engine
 /// subnautica</c> (<c>/de sn s14 engine</c>): Subnautica's loop for hydrogen (the Cyclops) and ion (the Seamoth),
 /// atmospheric stays SE's.
 /// </summary>
        public static class Engine
        {
            public const byte ModeSe = 0, ModeSubnautica = 1;
            /// <summary>SE's own thruster sound starts above this strength, per mille.</summary>
            public const int SeStartStrengthPermille = 100;
            public static string ModeName(byte m) { return m == ModeSubnautica ? "subnautica" : "se"; }
        }

        /// <summary>The C7 S14 block (SnStatus 0xC4-0xC7, after S11's 0xC0-0xC3): which thruster types Subnautica's loop plays.</summary>
        public static class C7
        {
            public const int Offset = 0xC4, Size = 4, Version = 1;
        }

        public static class C7Flags
        {
            /// <summary>Subnautica's engine mode is <c>subnautica</c>.</summary>
            public const byte SnLoops = 1;
            /// <summary>Its hydrogen loop (the Cyclops engine) is loaded: SE fades its own hydrogen thruster sound under water.</summary>
            public const byte Hydrogen = 2;
            /// <summary>Its ion loop (the Seamoth engine) is loaded: SE fades its own ion thruster sound under water.</summary>
            public const byte Ion = 4;
        }

        /// <summary>

 /// thruster flame, a held flame cone of the SN plugin drawn with S7p's above-water additive material. The Prawn's jet
 /// (Exosuit.fxcontrol.emitters[0].fx, rounds 3-4) draws with UWE/Particles/UBER underwater materials and shows nothing above
 /// the sea (his 16:49 session: jets on 11 of 11 flames, nothing seen).
 /// </summary>
        public static class Jets
        {
            /// </summary>
            public const int IdleFloorPermille = 300;
            /// <summary>The cone's half angle and its radius per metre of SE's flame radius.</summary>
            public const int ConeAngleDeg = 5;
            public const int ShapeRadiusPermille = 500;
            /// <summary>A particle's size at the tip (of its start size) and the share of its life it fades in over.</summary>
            public const int TipSizePermille = 350;
            public const int FadeInPermille = 80;
            public const int MaxParticles = 400;
            /// <summary>S14.N5V: per injected flame at least this many pixels brighter by VisibleLumaMin (sRGB luma) in its box.</summary>
            public const int VisibleMinPx = 400;
            public const int VisibleLumaMin = 30;
            public const int VisibleRefRows = 1440;
            /// <summary>S7p's flame rules the jet follows (S7pEffectRules.SeFlameLengthFactor, FlameLifetimeS, FlameLength's default length scale).</summary>
            public static int S7pFlameLengthPermille { get { return (int)Math.Round(S7pEffectRules.SeFlameLengthFactor * 1000.0); } }
            public static int S7pLifetimeMs { get { return (int)Math.Round(S7pEffectRules.FlameLifetimeS * 1000.0); } }
            public static int S7pDefaultLengthScalePermille { get { return S7pFxConventions.S7pLimits.LengthScaleDefaultMilli; } }
        }

        /// <summary>Point 2: the building sounds SE plays through <c>MyGuiAudio.PlaySound(MyGuiSounds)</c> (the HUD voice), muffled under the same gate.</summary>
        public static readonly string[] GuiSounds = { "HudColorBlock", "HudDeleteBlock", "HudPlaceBlock", "HudPlaceItem", "HudRotateBlock" };

        private static readonly string[] FormatLines =
        {
            "effect.name LowPassHelmet (SE's own, 1800 Hz) for every modifiable SE sound while the camera is under the sea (S2's camera SN y below Muffle.EyeYMm / 1000: the eye's own submersion, whatever the view state, its waterline hysteresis, dwell or ramp; owner 2026-10-24) and not in an airtight room; supersedes the S7 audio.muffle effect name",
            "effect.chain under water one low-pass per modifiable SE voice in both sound modes: where SE picked a pair's Real* recording, SelectCue answers its Arcade one (m_realistic false); chain = the effect applied + a Real* recording playing = MaxLowPass",
            "pan.stereo MyXAudio2.Apply3D of a voice with 2 channels or not the count SE used: one 1-channel X3DAudioCalculate (same listener and emitter; Matrix, RedirectToLfe) gives g[d]; matrix[d x channels + s] = g[d] x Downmix for 2 channels x SE's distance factor (1 - distance / max when the inner radius is 0; 0 silent); a 1-channel voice SE counted right keeps SE's matrix",
            "pan.own the ship-level sounds (MyShipSoundComponent) of the grid whose ship controller the player controls play SE's 2D path in both views (m_shouldPlay2D true after UpdateShouldPlay2D, SE restarting its loops once); every other ship SE's own 3D at its sound source",
            "pan.row s14.pan: per sampled SE voice the geometric pan = dot(direction to the voice, camera right) and the matrix pan = (R - L) / (R + L) of the voice's own output matrix; a 3D voice with |geometric| < Centred is never one-sided (quieter / louder < OneSided) and with |geometric| >= SignMin its sign is the geometry's",
            "jets.old while the jets are on and their material is found, scene-above's kit draws no thruster puff (oldFlames 0); otherwise the kit draws as before",
            "jets.length the cone reaches SE's flame length at the jet's strength (IdleFloor + (1 - IdleFloor) x intensity): S7p's FlameLength = S7pFlameLength x the flame's length scale (S7pDefaultLengthScale without one) x radius x strength x jetScale, in S7pLifetimeMs (speed = length / lifetime); start size S7p's FlameDiameter(radius, jetScale), TipSize of it at the tip, fading in over FadeIn of the life; rate S7p's FlameRate at the strength; radius ShapeRadius x SE's flame radius",
            "jets.visible S14.N5V: a Subnautica capture of injected above-water thruster flames, one per kind (hydrogen, ion, atmospheric), against the same pose with the jets off (the kit off in both): per flame at least VisibleMinPx pixels brighter by VisibleLumaMin within its projected box (VisibleMinPx at VisibleRefRows rows; x (rows / VisibleRefRows) ^ 2 for another capture height)",
            "effect.arcade a world without RealisticSound gets MyEntity3DSoundEmitter.EmitterMethods[3] = the emitter's own SelectEffect, so PlaySoundInternal applies the S7 postfix's answer",
            "effect.gui MyGuiAudio.PlaySound(MyGuiSounds) of the gui names: MyAudio.ApplyEffect on the returned voice under the same gate",
            "effect.reverb MyAudio.ApplyReverb true under the same gate when the player's EnableReverb is on; the reverb detector's preset back after",
            "jetpack.silence MyCharacterSoundComponent.SelectSound answers its EmptySoundPair while the swim latch holds the character in Flying with the jetpack not running",
            "thruster.se under water (seMixActive, block SN y < 0) a block SE's ship sound system has not silenced plays SE's own cue of its definition (muffled by effect.name), its emitter's VolumeMultiplier = the envelope level x gain(strength), or fades to 0 over SeThrusterFadeMs when Subnautica plays its type (c7.s14); a silenced block is never written; above water nothing is touched (a value S14 wrote is given back once)",
            "thruster.ship a ship's grid-level thruster sound per type (MyShipSoundComponent.UpdateThrusterVolume) under water: its main emitter x the envelope of the type's strongest thruster, or main, idle and push emitters x a fade to 0 over SeThrusterFadeMs when Subnautica plays the type; above water untouched",
            "thruster.start under water SE's own sound starts on the tick the strength passes SeStartStrength (MyThrust.UpdateSoundState called by the plugin) instead of SE's 100-tick cadence",
            "thruster.sn the envelope (level and rpm or strength to their targets at full scale per AttackMs up and per ReleaseMs down; the sound stops only at level 0; volume = level x gain) on whichever source plays",
            "engine.mode se (default): no Subnautica loop; subnautica: hydrogen = SubControl.engineRPMManager.engineRpmSFX (Cyclops), ion = SeaMoth.engineSound.engineRpmSFX, atmospheric and other = SE's own; one loop per grid and type",
            "c7.s14 SnStatus 0xc4-0xc7 written by Subnautica every frame: version, flags (SnLoops, Hydrogen, Ion = the types its loop plays), 2 reserved; version 0 = absent (SE keeps its own thruster sound)",
            "thruster.gain gain(rpm) = GainMin + (1 - GainMin) x rpm ^ GainExponent",
            "listener.sn every FMOD listener = the main camera's pose (position, forward, up; attenuation at the camera) after StudioListener.SetListenerLocation while the bridge drives the camera",
            "weapons.worldmod Data/WaterConfig.xml BlockConfigs: the Water Mod's 20 weapon entries, FunctionalUnderwater true, every other attribute equal",
            "jets.above per above-water thruster flame a held flame cone of the SN plugin along the flame (Local space, Cone ConeAngleDeg, MaxParticles), its particles drawn with S7p's above-water additive material (Unity's Legacy Shaders/Particles/Additive, else Particles/Additive or Mobile/Particles/Additive, on a soft dot made in code) in SE's flame colour (S7p's thruster colour through its Glow); not the Prawn's jet: Exosuit.fxcontrol.emitters[0].fx draws with UWE/Particles/UBER underwater materials (waterBlob_01_sheet_premul_underwaterClipCloseLit, bubble_01_sheet_premul_underwaterCameraVertexLit) and shows nothing above the sea",
        };

        private static string _text;
        private static ulong _hash;

        private static void C(StringBuilder sb, string group, string name, int v)
        {
            sb.Append("const ").Append(group).Append(' ').Append(name).Append(' ').Append(v.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        /// <summary>
 /// The canonical text (CONTRACT §5.4 "S14 conventions"; ASCII, '\n' after every line): the header, the constant
 /// groups, the gui names, the format lines sorted ordinally.
 /// </summary>
        public static string CanonicalText()
        {
            if (_text != null) return _text;
            var sb = new StringBuilder(4096);
            sb.Append(Header).Append('\n');
            sb.Append("effect ").Append(UnderwaterEffect).Append('\n');
            C(sb, "Effect", "UnderwaterCutoffHz", Effect.UnderwaterCutoffHz);
            C(sb, "Effect", "DryCutoffMinHz", Effect.DryCutoffMinHz);
            C(sb, "Effect", "SampleRateHz", Effect.SampleRateHz);
            C(sb, "Muffle", "EyeYMm", Muffle.EyeYMm);
            C(sb, "Muffle", "DeadbandMm", Muffle.DeadbandMm);
            C(sb, "Envelope", "AttackMs", Envelope.AttackMs);
            C(sb, "Envelope", "ReleaseMs", Envelope.ReleaseMs);
            C(sb, "Envelope", "GainMinPermille", Envelope.GainMinPermille);
            C(sb, "Envelope", "GainExponentPermille", Envelope.GainExponentPermille);
            C(sb, "Envelope", "SeThrusterFadeMs", Envelope.SeThrusterFadeMs);
            C(sb, "Listener", "ToleranceMm", Listener.ToleranceMm);
            C(sb, "Listener", "ToleranceCentiDeg", Listener.ToleranceCentiDeg);
            C(sb, "Jetpack", "GraceMs", Jetpack.GraceMs);
            C(sb, "Rows", "PeriodMs", Rows.PeriodMs);
            C(sb, "Pan", "OneSidedPermille", Pan.OneSidedPermille);
            C(sb, "Pan", "CentredPermille", Pan.CentredPermille);
            C(sb, "Pan", "SignMinPermille", Pan.SignMinPermille);
            C(sb, "Pan", "DownmixPermille", Pan.DownmixPermille);
            C(sb, "Chain", "MaxLowPass", Chain.MaxLowPass);
            C(sb, "Weapons", "Configs", Weapons.Configs);
            C(sb, "Engine", "ModeSe", Engine.ModeSe);
            C(sb, "Engine", "ModeSubnautica", Engine.ModeSubnautica);
            C(sb, "Engine", "SeStartStrengthPermille", Engine.SeStartStrengthPermille);
            C(sb, "C7", "Offset", C7.Offset);
            C(sb, "C7", "Size", C7.Size);
            C(sb, "C7", "Version", C7.Version);
            C(sb, "C7Flags", "SnLoops", C7Flags.SnLoops);
            C(sb, "C7Flags", "Hydrogen", C7Flags.Hydrogen);
            C(sb, "C7Flags", "Ion", C7Flags.Ion);
            C(sb, "Jets", "IdleFloorPermille", Jets.IdleFloorPermille);
            C(sb, "Jets", "ConeAngleDeg", Jets.ConeAngleDeg);
            C(sb, "Jets", "ShapeRadiusPermille", Jets.ShapeRadiusPermille);
            C(sb, "Jets", "TipSizePermille", Jets.TipSizePermille);
            C(sb, "Jets", "FadeInPermille", Jets.FadeInPermille);
            C(sb, "Jets", "MaxParticles", Jets.MaxParticles);
            C(sb, "Jets", "VisibleMinPx", Jets.VisibleMinPx);
            C(sb, "Jets", "VisibleLumaMin", Jets.VisibleLumaMin);
            C(sb, "Jets", "VisibleRefRows", Jets.VisibleRefRows);
            C(sb, "Jets", "S7pFlameLengthPermille", Jets.S7pFlameLengthPermille);
            C(sb, "Jets", "S7pLifetimeMs", Jets.S7pLifetimeMs);
            C(sb, "Jets", "S7pDefaultLengthScalePermille", Jets.S7pDefaultLengthScalePermille);
            foreach (string g in GuiSounds) sb.Append("gui ").Append(g).Append('\n');
            var formats = (string[])FormatLines.Clone();
            Array.Sort(formats, string.CompareOrdinal);
            foreach (string f in formats) sb.Append("format ").Append(f).Append('\n');
            _text = sb.ToString();
            return _text;
        }

        /// <summary>FNV-1a 64 of <see cref="CanonicalText"/>.</summary>
        public static ulong Hash()
        {
            if (_hash == 0) _hash = LayoutHash.Fnv1a64Ascii(CanonicalText());
            return _hash;
        }
    }

    /// <summary>The S14 rules both plugins use (pure; tested in Wire.Tests; the analyzer's twin computes the same numbers).</summary>
    public static class S14Rules
    {
        /// <summary>Point 1: SelectSound answers the empty pair (SE's own stop path for every looping state emitter).</summary>
        public static bool SilenceJetpack(bool enabled, bool swimHeld, bool jetpackRunning, bool flying, bool mutantOff)
        {
            return enabled && !mutantOff && swimHeld && !jetpackRunning && flying;
        }

        /// <summary>Point 2: an Arcade world (RealisticSound false) gets the implicit-effect delegate that realistic worlds have.</summary>
        public static bool ArcadeRoute(bool enabled, bool realisticSound, bool mutantOff)
        {
            return enabled && !mutantOff && !realisticSound;
        }

        /// <summary>Point 2: one of the building GUI sounds muffled under water (ordinal names of <c>MyGuiSounds</c>).</summary>
        public static bool IsMuffledGui(string name)
        {
            return name != null && Array.IndexOf(S14Conventions.GuiSounds, name) >= 0;
        }

        /// <summary>format effect.name (ext s14 audio 5): the eye is under the sea (S2's camera SN y, NaN = no camera: dry).</summary>
        public static bool EyeUnder(double eyeSnY)
        {
            return !double.IsNaN(eyeSnY) && eyeSnY * 1000.0 < S14Conventions.Muffle.EyeYMm;
        }

        /// <summary>Point 4: the thruster is under water for the sound rules (the underwater mix on and the block below the surface).</summary>
        public static bool ThrusterSubmerged(bool mixActive, double blockSnY)
        {
            return mixActive && blockSnY < 0.0;
        }

        /// <summary>Point 4b: Subnautica plays a loop for this thruster type in this mode (the asset loaded).</summary>
        public static bool SnPlaysType(byte mode, ushort kind, bool hydrogenLoaded, bool ionLoaded)
        {
            if (mode != S14Conventions.Engine.ModeSubnautica) return false;
            if (kind == S7Conventions.ThrusterKind.Hydrogen) return hydrogenLoaded;
            if (kind == S7Conventions.ThrusterKind.Ion) return ionLoaded;
            return false;
        }

        /// <summary>Point 4b: the C7 S14 flags Subnautica publishes.</summary>
        public static byte C7FlagsOf(byte mode, bool hydrogenLoaded, bool ionLoaded)
        {
            if (mode != S14Conventions.Engine.ModeSubnautica) return 0;
            byte f = S14Conventions.C7Flags.SnLoops;
            if (hydrogenLoaded) f |= S14Conventions.C7Flags.Hydrogen;
            if (ionLoaded) f |= S14Conventions.C7Flags.Ion;
            return f;
        }

        /// <summary>Point 4b, SE side: Subnautica's loop covers this type (a fresh C7 S14 block with its flag).</summary>
        public static bool SnCovers(bool c7Fresh, byte c7Flags, ushort kind)
        {
            if (!c7Fresh || (c7Flags & S14Conventions.C7Flags.SnLoops) == 0) return false;
            if (kind == S7Conventions.ThrusterKind.Hydrogen) return (c7Flags & S14Conventions.C7Flags.Hydrogen) != 0;
            if (kind == S7Conventions.ThrusterKind.Ion) return (c7Flags & S14Conventions.C7Flags.Ion) != 0;
            return false;
        }

        /// <summary>
 /// Point 4b, SE side: SE's own thruster emitter's VolumeMultiplier target. Above water 1 (SE's own); under water 0 when
 /// Subnautica's loop covers the type, else the envelope's volume (level x gain(strength)); S14.M7 = 1 under water too.
 /// </summary>
        public static double SeThrusterVolume(bool submerged, bool covered, double envelopeVolume, bool mutantLoud)
        {
            if (!submerged || mutantLoud) return 1.0;
            if (covered) return 0.0;
            return Clamp01(envelopeVolume);
        }

        /// <summary>
 /// Point 4: a linear envelope step toward <paramref name="target"/> (0..1 scale): full scale per <paramref name="attackMs"/>
 /// up, per <paramref name="releaseMs"/> down; a non-positive time jumps.
 /// </summary>
        public static double Step(double current, double target, double dtMs, double attackMs, double releaseMs)
        {
            if (double.IsNaN(current)) current = 0;
            if (double.IsNaN(target)) target = 0;
            if (!(dtMs > 0)) return current;
            if (target > current)
            {
                if (!(attackMs > 0)) return target;
                double v = current + dtMs / attackMs;
                return v > target ? target : v;
            }
            if (target < current)
            {
                if (!(releaseMs > 0)) return target;
                double v = current - dtMs / releaseMs;
                return v < target ? target : v;
            }
            return current;
        }

        /// <summary>Point 4: the engine loop's level for an rpm (small corrections quieter): min + (1 - min) rpm^exponent.</summary>
        public static double EngineGain(double rpm)
        {
            double r = Clamp01(rpm);
            double min = S14Conventions.Envelope.GainMinPermille / 1000.0;
            return min + (1.0 - min) * Math.Pow(r, S14Conventions.Envelope.GainExponentPermille / 1000.0);
        }

        /// <summary>Point 4: the FMOD event volume of the engine loop.</summary>
        public static double EngineVolume(double level, double rpm)
        {
            return Clamp01(level) * EngineGain(rpm);
        }

        /// <summary>SE's Hz → XAudio2 filter frequency.</summary>
        public static double XAudioFrequency(double hz)
        {
            double v = 2.0 * Math.Sin(3.14 * hz / S14Conventions.Effect.SampleRateHz);
            return v < 0 ? 0 : v > 1 ? 1 : v;
        }

        /// <summary>The inverse (readback of a voice's filter): XAudio2 frequency → Hz (NaN for an invalid input).</summary>
        public static double CutoffHz(double xaudioFrequency)
        {
            if (double.IsNaN(xaudioFrequency) || xaudioFrequency < 0) return double.NaN;
            double h = xaudioFrequency / 2.0;
            if (h > 1.0) h = 1.0;
            return Math.Asin(h) * S14Conventions.Effect.SampleRateHz / 3.14;
        }

        /// <summary>Point 4a: the FMOD listener matches the camera.</summary>
        public static bool ListenerOk(double offsetM, double angleDeg)
        {
            return offsetM <= S14Conventions.Listener.ToleranceMm / 1000.0 && angleDeg <= S14Conventions.Listener.ToleranceCentiDeg / 100.0;
        }

        /// <summary>jets.length: the flame's strength, the idle floor under SE's intensity (0..1).</summary>
        public static double JetStrength(double intensity)
        {
            double floor = S14Conventions.Jets.IdleFloorPermille / 1000.0;
            return floor + (1.0 - floor) * Clamp01(intensity);
        }

        /// <summary>jets.length: the cone's length (m), S7p's FlameLength at the jet's strength (0 without a radius or knob).</summary>
        public static double JetLengthM(double intensity, double radiusM, double lengthScale, double knob)
        {
            return S7pEffectRules.FlameLength((float)JetStrength(intensity), (float)radiusM, (float)lengthScale, (float)knob);
        }

        /// <summary>jets.length: the start size (m), S7p's FlameDiameter.</summary>
        public static double JetDiameterM(double radiusM, double knob)
        {
            return S7pEffectRules.FlameDiameter((float)radiusM, (float)knob);
        }

        /// <summary>jets.length: the particles' speed (m/s) so the cone reaches its length in S7p's flame lifetime.</summary>
        public static double JetSpeed(double lengthM)
        {
            return S7pEffectRules.FlameSpeed((float)lengthM);
        }

        /// <summary>jets.length: particles per second, S7p's FlameRate at the strength.</summary>
        public static double JetRate(double intensity, double radiusM)
        {
            return S7pEffectRules.FlameRate((float)JetStrength(intensity), (float)radiusM, 1f);
        }

        /// <summary>jets.length: the cone's radius (m) for SE's flame radius.</summary>
        public static double JetShapeRadiusM(double radiusM)
        {
            double r = (radiusM > 0 ? radiusM : 0) * S14Conventions.Jets.ShapeRadiusPermille / 1000.0;
            return r < 0.02 ? 0.02 : r;
        }

        /// <summary>jets.visible: the pixels an injected flame needs on a capture <paramref name="rows"/> high (VisibleMinPx at VisibleRefRows).</summary>
        public static int JetVisibleMinPx(int rows)
        {
            double k = rows > 0 ? (double)rows / S14Conventions.Jets.VisibleRefRows : 1.0;
            return Math.Max(1, (int)Math.Round(S14Conventions.Jets.VisibleMinPx * k * k));
        }

        /// <summary>jets.visible: an injected flame is seen when enough of its box's pixels got brighter by VisibleLumaMin.</summary>
        public static bool JetVisible(int brighterPx, int rows)
        {
            return brighterPx >= JetVisibleMinPx(rows);
        }

        /// <summary>
 /// Round 4, pan.stereo: the output matrix (XAudio2's layout, index d x channels + s) of a voice with <paramref name="channels"/>
 /// (1 or 2) source channels from a valid 1-channel X3DAudio result <paramref name="mono"/> (one gain per destination) and
 /// SE's distance factor; null for anything else.
 /// </summary>
        public static float[] PanMatrix(float[] mono, int channels, double distanceFactor)
        {
            if (mono == null || mono.Length == 0 || channels < 1 || channels > 2) return null;
            int dst = mono.Length;
            double k = (channels == 2 ? S14Conventions.Pan.DownmixPermille / 1000.0 : 1.0) * Clamp01(distanceFactor);
            var m = new float[channels * dst];
            for (int d = 0; d < dst; d++)
                for (int s = 0; s < channels; s++)
                    m[d * channels + s] = (float)(mono[d] * k);
            return m;
        }

        /// <summary>SE's linear distance factor on top of X3DAudio's curve (MyXAudio2.Apply3D: an inner radius 0, i.e. a stereo device).</summary>
        public static double DistanceFactor(double distance, double maxDistance, bool silent, bool innerRadiusZero)
        {
            if (!innerRadiusZero) return 1.0;
            if (silent || !(maxDistance > 0) || double.IsNaN(distance)) return 0.0;
            return Clamp01(1.0 - distance / maxDistance);
        }

        /// <summary>pan.row: the left (destination 0) and right (destination 1) sums of an XAudio2 matrix; a mono device hears both the same.</summary>
        public static void MatrixLR(float[] m, int src, int dst, out double left, out double right)
        {
            left = right = 0;
            if (m == null || src < 1 || dst < 1 || m.Length < src * dst) return;
            for (int s = 0; s < src; s++)
            {
                left += Math.Abs(m[s]);
                right += Math.Abs(m[(dst > 1 ? 1 : 0) * src + s]);
            }
        }

        /// <summary>pan.row: (R - L) / (R + L), NaN when the voice is silent.</summary>
        public static double MatrixPan(double left, double right)
        {
            double t = left + right;
            return t > 1e-6 ? (right - left) / t : double.NaN;
        }

        /// <summary>pan.row: the quieter side below OneSided of the louder one (an audible voice heard in one ear).</summary>
        public static bool OneSided(double left, double right)
        {
            double hi = Math.Max(left, right), lo = Math.Min(left, right);
            return hi > 1e-4 && lo < S14Conventions.Pan.OneSidedPermille / 1000.0 * hi;
        }

        /// <summary>pan.row: a voice roughly in front of or behind the listener (|geometric pan| below Centred).</summary>
        public static bool PanCentred(double geometric)
        {
            return !double.IsNaN(geometric) && Math.Abs(geometric) < S14Conventions.Pan.CentredPermille / 1000.0;
        }

        /// <summary>pan.row: beyond SignMin the matrix pan has the geometry's sign (true below SignMin: not judged).</summary>
        public static bool PanSignAgrees(double geometric, double matrixPan)
        {
            if (double.IsNaN(geometric) || Math.Abs(geometric) < S14Conventions.Pan.SignMinPermille / 1000.0) return true;
            if (double.IsNaN(matrixPan)) return false;
            return Math.Sign(geometric) == Math.Sign(matrixPan);
        }

        /// <summary>effect.chain: the low-pass treatments of a modifiable voice under water (the effect, a Real* recording).</summary>
        public static int ChainLength(bool effectApplied, bool realRecording)
        {
            return (effectApplied ? 1 : 0) + (realRecording ? 1 : 0);
        }

        /// <summary>jets.old: scene-above's kit leaves the thruster puffs to the jets (on, their material found, S14.M6 off).</summary>
        public static bool KitThrusterOff(bool enabled, bool jetsOn, bool mutantNoJets, bool sourceFound)
        {
            return enabled && jetsOn && !mutantNoJets && sourceFound;
        }

        public static double Clamp01(double v)
        {
            if (double.IsNaN(v)) return 0;
            return v < 0 ? 0 : v > 1 ? 1 : v;
        }
    }

    /// <summary>What happened to an envelope this frame (for the s14.engine rows).</summary>
    public enum S14EnvEvent
    {
        None = 0,
        /// <summary>Wanted again after silence: the attack starts.</summary>
        Start,
        /// <summary>The level reached 1 (full) after a start.</summary>
        AttackDone,
        /// <summary>The thrust ended: the release starts (the sound keeps playing).</summary>
        Release,
        /// <summary>The level reached 0: the sound stops.</summary>
        Stopped,
        /// <summary>Wanted again during the release (no restart).</summary>
        Resume,
    }

    /// <summary>
 /// Point 4b: one thruster sound's envelope (CONTRACT §5.4 "S14 conventions", format thruster.sn): the level and the rpm (or
 /// SE's strength) move to their targets at full scale per AttackMs up and per ReleaseMs down; the sound plays until the
 /// level is 0; volume = level x gain(rpm). Used by Subnautica's engine loop and SE's own thruster emitter alike.
 /// </summary>
    public sealed class S14EngineEnv
    {
        public double Level, RpmS;
        public bool Want, Playing;
        public double StartedAt = -1, ReleasedAt = -1;
        public bool AttackLogged;
        /// <summary>The measured times of the last attack (start → level 1) and release (end → level 0), ms; NaN until measured.</summary>
        public double LastAttackMs = double.NaN, LastReleaseMs = double.NaN;
        public long Starts, Resumes, Stops, Attacks;

        /// <summary>
 /// One frame: <paramref name="want"/> = the thrust is on, <paramref name="rpm"/> its level (0..1), <paramref name="nowS"/>
 /// the clock in seconds. Returns what happened; <see cref="Playing"/> says whether the sound plays. With
 /// <paramref name="noEnvelope"/> (S14.M5, S14.M7) the level jumps.
 /// </summary>
        public S14EnvEvent Step(bool want, double rpm, double dtMs, double nowS, bool noEnvelope)
        {
            S14EnvEvent ev = S14EnvEvent.None;
            bool wasPlaying = Playing;
            double target = want ? 1.0 : 0.0;
            double rpmTarget = want ? S14Rules.Clamp01(rpm) : 0.0;
            if (want && !Want)
            {
                if (!wasPlaying) { ev = S14EnvEvent.Start; StartedAt = nowS; AttackLogged = false; Starts++; }
                else
                {
                    // wanted again during the release: no restart; the attack goes on from the current level
                    ev = S14EnvEvent.Resume;
                    Resumes++;
                    StartedAt = nowS - Level * S14Conventions.Envelope.AttackMs / 1000.0;
                    AttackLogged = Level >= 1.0;
                    ReleasedAt = -1;
                }
            }
            else if (!want && Want && wasPlaying) { ev = S14EnvEvent.Release; ReleasedAt = nowS; }
            Want = want;
            if (noEnvelope)
            {
                Level = target;
                RpmS = rpmTarget;
            }
            else
            {
                Level = S14Rules.Step(Level, target, dtMs, S14Conventions.Envelope.AttackMs, S14Conventions.Envelope.ReleaseMs);
                // the rpm follows at the same rates; while releasing it holds its last value so the tail keeps its pitch
                if (want) RpmS = S14Rules.Step(RpmS, rpmTarget, dtMs, S14Conventions.Envelope.AttackMs, S14Conventions.Envelope.ReleaseMs);
            }
            Playing = want || Level > 0.0;
            if (want && Level >= 1.0 && !AttackLogged && StartedAt >= 0)
            {
                AttackLogged = true;
                LastAttackMs = (nowS - StartedAt) * 1000.0;
                Attacks++;
                if (ev == S14EnvEvent.None) ev = S14EnvEvent.AttackDone;
            }
            if (wasPlaying && !Playing)
            {
                LastReleaseMs = ReleasedAt >= 0 ? (nowS - ReleasedAt) * 1000.0 : double.NaN;
                ev = S14EnvEvent.Stopped; // a release and its end in one frame (a mutant) report the end
                Stops++;
                StartedAt = -1;
                ReleasedAt = -1;
            }
            return ev;
        }

        /// <summary>The volume: level x gain(rpm) (no envelope: 1 while playing, the earlier constant level).</summary>
        public double Volume(bool noEnvelope) { return noEnvelope ? (Playing ? 1.0 : 0.0) : S14Rules.EngineVolume(Level, RpmS); }

        /// <summary>The rpm parameter sent to a Subnautica loop (no envelope: the raw rpm, S7's).</summary>
        public double RpmParam(bool noEnvelope, double rawRpm) { return noEnvelope ? rawRpm : RpmS; }

        /// <summary>Back to silence without an event (the thruster left the water, the source changed).</summary>
        public void Reset()
        {
            Level = RpmS = 0;
            Want = Playing = false;
            StartedAt = ReleasedAt = -1;
            AttackLogged = false;
        }
    }

    /// <summary>The C7 S14 block codec (SnStatus 0xC4-0xC7; <c>format c7.s14</c>).</summary>
    public static unsafe class S14Wire
    {
        public static void WriteStatus(ref SnStatus st, byte flags)
        {
            fixed (uint* p = &st.seq)
            {
                byte* b = (byte*)p + S14Conventions.C7.Offset;
                b[0] = (byte)S14Conventions.C7.Version;
                b[1] = flags;
                b[2] = 0;
                b[3] = 0;
            }
        }

        /// <summary>False when the record carries no S14 block (version 0 or unknown: a Subnautica plugin before S14 v2).</summary>
        public static bool TryReadStatus(ref SnStatus st, out byte flags)
        {
            flags = 0;
            fixed (uint* p = &st.seq)
            {
                byte* b = (byte*)p + S14Conventions.C7.Offset;
                if (b[0] != S14Conventions.C7.Version) return false;
                flags = b[1];
            }
            return true;
        }
    }
}
