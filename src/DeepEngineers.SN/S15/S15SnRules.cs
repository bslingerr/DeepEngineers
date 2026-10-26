using System;
using System.Collections.Generic;
using DeepEngineers.Wire;

namespace DeepEngineers.SN
{
    // =================================================================================================================
 // S15 "Gameplay and immersion polish", the Unity-free part of the Subnautica side (compiled into tests/DeepEngineers.SN.Tests;
 // DevMutants.cs names S15SnMutants). CONTRACT §5.4 "S15 conventions": the voice lines from the C5 block (one per counter
 // increment, S16SeqReader; one line at a time, format voice.mix), the audio groups' levels (format audio.groups) and the
 // sky bodies from the C6 frame and list.
 // =================================================================================================================

 /// <summary>The S15 seeded failures of the Subnautica side.</summary>
    internal static class S15SnMutants
    {
        /// <summary>
 /// S15.M3: each disc placed along the body's SE-world direction, not mapped to Subnautica's axes: S15.SKYDIR red.
 /// </summary>
        public const string Unmapped = "S15.M3";
        /// <summary>S15.M5: Subnautica's moon is left on (only its planet hidden): S15.MOONS red.</summary>
        public const string MoonOn = "S15.M5";
        /// <summary>S15.M6: the disc's radius from the diameter (twice the size): S15.SKYSIZE red.</summary>
        public const string SizeDiameter = "S15.M6";
        /// <summary>S15.M7: the voice plays while SE's audio is live (the S2 rule ignored): S15.S2 red.</summary>
        public const string VoiceIgnoresS2 = "S15.M7";
        /// <summary>S15.M11: the block lights keep their day intensity at night: S15.NIGHT red.</summary>
        public const string NoNight = "S15.M11";
        /// <summary>S15.M12: no lens splash when the camera surfaces: S15.SPLASH red.</summary>
        public const string NoSplash = "S15.M12";
        /// <summary>S15.M13: the sonar plays its sound without the screen pulse: S15.SONAR red.</summary>
        public const string SonarNoFx = "S15.M13";
        /// <summary>S15.M14: no base hum in an airtight room: S15.SEABASE red.</summary>
        public const string NoHum = "S15.M14";
        /// <summary>S15.M15: the ships scare no fish: S15.FISH red.</summary>
        public const string NoScare = "S15.M15";
        /// <summary>
 /// S15.M19: Subnautica's audio cut above water as before (format audio.groups off: the S2 rule's bus:/ fade and its
 /// above-water mute, so the music stops and a line under way goes silent): S15.MUSIC and S15.LINE red.
 /// </summary>
        public const string MusicCut = "S15.M19";
        /// <summary>S15.M20: Subnautica's story goals and the Aurora's countdown run (formats story.off, aurora.off ignored): S15.STORY red.</summary>
        public const string StoryOn = "S15.M20";
        public static readonly string[] All = { Unmapped, MoonOn, SizeDiameter, VoiceIgnoresS2, NoNight, NoSplash, SonarNoFx, NoHum, NoScare, MusicCut, StoryOn };
    }

    /// <summary>One line to play.</summary>
    internal struct S15SnLine
    {
        public int Event;
        public uint Seq;
        public double QueuedS;
        /// <summary>FirstDeep: the RebreatherDepthWarnings alert's index (the depth passed).</summary>
        public int Variant;
    }

    /// <summary>
 /// The voice lines of one frame (format c5.s15 and voice.mix): one per counter increment (S16's reader: the first value
 /// is the baseline, several at once are one line, a new worldEpoch re-baselines), queued and released one at a time
 /// <c>QueueGapMs</c> after the last voice (S15's own or S16's), at most <c>QueueMax</c> waiting (a new line beyond that dropped, a welcome replacing the newest other line).
 /// </summary>
    internal sealed class S15SnVoice
    {
        private readonly S16SeqReader[] _readers = new S16SeqReader[S15Conventions.Event.Count + 1];
        private readonly List<S15SnLine> _queue = new List<S15SnLine>();
        private double _lastVoiceS = -1e9;
        public long Frames, Absent, Lines, Dropped;

        public S15SnVoice()
        {
            for (int i = 1; i <= S15Conventions.Event.Count; i++) _readers[i] = new S16SeqReader();
        }

        public int Waiting { get { return _queue.Count; } }

        public long Coalesced
        {
            get { long c = 0; for (int i = 1; i <= S15Conventions.Event.Count; i++) c += _readers[i].Coalesced; return c; }
        }

        public void Reset()
        {
            for (int i = 1; i <= S15Conventions.Event.Count; i++) _readers[i].Reset();
            _queue.Clear();
            _lastVoiceS = -1e9;
        }

        /// <summary>A voice was heard now (S16's hull voice): the next S15 line waits QueueGapMs from here.</summary>
        public void Heard(double nowS) { if (nowS > _lastVoiceS) _lastVoiceS = nowS; }

        /// <summary>
 /// One frame: the block (<paramref name="have"/> false = no record or no block: nothing new, baselines kept); returns
 /// the line to play now, if any (the caller plays it and calls <see cref="Heard"/>).
 /// </summary>
        public bool Step(bool have, ref S15WireState w, double nowS, out S15SnLine line) { return Step(have, ref w, null, nowS, out line); }

        /// <summary>One frame of the C5 block (format c5. the events block carries none).</summary>
        public bool Step(bool have, ref S15WireState w, S15EventsState e, double nowS, out S15SnLine line)
        {
            Frames++;
            line = default(S15SnLine);
            if (!have) Absent++;
            else
                for (int ev = 1; ev <= S15Conventions.Event.C5Count; ev++)
                    if (_readers[ev].Step(w.WorldEpoch, w.SeqOf(ev)) > 0) Add(ev, w.SeqOf(ev), 0, nowS);
            return Release(nowS, out line);
        }

        /// <summary>The next line when the gap allowed it (also without a new block this frame).</summary>
        public bool Release(double nowS, out S15SnLine line)
        {
            line = default(S15SnLine);
            if (_queue.Count == 0 || (nowS - _lastVoiceS) * 1000.0 < S15Conventions.Voice.QueueGapMs) return false;
            line = _queue[0];
            _queue.RemoveAt(0);
            Lines++;
            return true;
        }

        private void Add(int ev, uint seq, int variant, double nowS)
        {
            var line2 = new S15SnLine { Event = ev, Seq = seq, QueuedS = nowS, Variant = variant };
            if (_queue.Count >= S15Conventions.Voice.QueueMax)
            {
                //  the newest line is dropped, never a welcome (a welcome replaces the newest other line)
                int victim = -1;
                if (IsWelcome(ev))
                    for (int k = _queue.Count - 1; k >= 0; k--) if (!IsWelcome(_queue[k].Event)) { victim = k; break; }
                Dropped++;
                if (victim < 0) return;
                _queue.RemoveAt(victim);
            }
            _queue.Add(line2);
        }

        private static bool IsWelcome(int ev) { return ev == S15Conventions.Event.Welcome || ev == S15Conventions.Event.SmallWelcome; }
    }

    /// <summary>
 /// format audio.groups, the levels (pure; the FMOD side is S15SnMix): the music and the voice groups follow the S2 rule's
 /// target between their above-water level and 1 over Mix.CrossfadeMs (linear, S2's own Crossfade); the effects group is
 /// the S2 rule's own fade. The above-water levels are the pinned ones until de.s15 music / voiceAbove tunes them.
 /// </summary>
    internal sealed class S15SnGroups
    {
        public readonly Crossfade Music = new Crossfade(1f), Voice = new Crossfade(1f);
        public float MusicAbove = S15Conventions.Mix.MusicAbovePermille / 1000f, VoiceAbove = S15Conventions.Mix.VoiceAbovePermille / 1000f;
        public float MusicTarget = 1f, VoiceTarget = 1f;

        /// <summary>One frame at the S2 rule's effective target <paramref name="s2Target"/> (1 under water, 0 above).</summary>
        public void Step(ulong nowMs, float s2Target, out float music, out float voice)
        {
            MusicTarget = (float)S15MixRules.GroupTarget(s2Target, MusicAbove);
            VoiceTarget = (float)S15MixRules.GroupTarget(s2Target, VoiceAbove);
            music = Music.Step(MusicTarget, SwingMs(MusicAbove), nowMs);
            voice = Voice.Step(VoiceTarget, SwingMs(VoiceAbove), nowMs);
        }

        /// <summary>
 /// The Crossfade's full 0 to 1 time that makes the group's surface swing (1 to <paramref name="above"/>) take
 /// Mix.CrossfadeMs (format audio.groups "over Mix.CrossfadeMs"): Crossfade moves 1 / its time per ms, so the 0.4 swing of
 /// 0.6 took 0.4 x 2000 = 800 ms (run 20261024-133305: fades 750-1000 ms).
 /// </summary>
        public static float SwingMs(float above)
        {
            float swing = Math.Abs(1f - Math.Max(0f, Math.Min(1f, above)));
            return S15Conventions.Mix.CrossfadeMs / Math.Max(swing, 0.05f);
        }

        public bool Fading { get { return Music.Fading || Voice.Fading; } }

        public void Reset(ulong nowMs) { Music.Set(1f, nowMs); Voice.Set(1f, nowMs); }
    }

    /// <summary>
 /// format voice.end: one played line followed to its end (pure part): the voice group's lowest level, the master mute and
 /// the S2 target seen while it played, its timeline position against its length.
 /// </summary>
    internal sealed class S15SnLineWatch
    {
        public int Event;
        public uint Seq;
        public double StartS;
        public int LengthMs = -1, LastPosMs;
        public float VoiceMin = 1f;
        public bool Crossed, Muted, StartedUnder;

        public void Sample(int posMs, float voiceLevel, bool muted, float s2Target)
        {
            if (posMs > LastPosMs) LastPosMs = posMs;
            if (voiceLevel < VoiceMin) VoiceMin = voiceLevel;
            if (muted) Muted = true;
            if (s2Target < 0.5f) Crossed = true;
        }

        /// <summary>The line reached its end: its last position within 250 ms of its length (or its length unknown).</summary>
        public bool Complete { get { return LengthMs <= 0 || LastPosMs >= LengthMs - 250; } }
    }

    /// <summary>The lens splash rule (format lens.splash): the camera rises through the sea after SplashMinUnderMs under it.</summary>
    internal sealed class S15SnSplash
    {
        private double _underSinceS = double.NaN;
        private bool _wasUnder;

        public void Reset() { _underSinceS = double.NaN; _wasUnder = false; }

        /// <summary>True on the frame the camera surfaces after a long enough dive.</summary>
        public bool Step(double camY, double nowS)
        {
            bool under = camY < 0;
            bool splash = false;
            if (under && !_wasUnder) _underSinceS = nowS;
            if (!under && _wasUnder && !double.IsNaN(_underSinceS) && (nowS - _underSinceS) * 1000.0 >= S15Conventions.Picks.SplashMinUnderMs) splash = true;
            _wasUnder = under;
            return splash;
        }
    }

    /// <summary>
 /// One voice at a time both ways: the shared clock of the last S15 line and the hold of
 /// S16's hull voice. S15 waits QueueGapMs after any voice (it hears S16's through S16Sn.Voices); S16's hull voice waits
 /// QueueGapMs after an S15 line (held, never dropped: <see cref="HoldS16"/>, S15 hook in S16Sn); S16's creaks and groans
 /// duck under both (<see cref="SinceVoiceMs"/>). Main thread.
 /// </summary>
    internal static class S15SnVoiceClock
    {
        /// <summary>Time.unscaledTime of the last S15 line played (-1e9 = none).</summary>
        public static double LastS15S = -1e9;
        public static bool HeldS16;
        public static long S16Held;

        public static void Reset() { LastS15S = -1e9; HeldS16 = false; }

        public static bool InS15Gap(double nowS) { return (nowS - LastS15S) * 1000.0 < S15Conventions.Voice.QueueGapMs; }

        /// <summary>S16's hook: its hull voice of this frame is held while an S15 line is inside its gap, and released after.</summary>
        public static void HoldS16(ref bool voice, double nowS)
        {
            if (voice)
            {
                if (InS15Gap(nowS)) { voice = false; if (!HeldS16) S16Held++; HeldS16 = true; }
                else HeldS16 = false;
            }
            else if (HeldS16 && !InS15Gap(nowS)) { HeldS16 = false; voice = true; }
        }

        /// <summary>S16's hook: ms since the last voice of either slice (S16's own <paramref name="s16LastVoiceS"/> or an S15 line); -1 = none.</summary>
        public static double SinceVoiceMs(double nowS, double s16LastVoiceS)
        {
            double last = Math.Max(s16LastVoiceS < 0 ? -1e9 : s16LastVoiceS, LastS15S);
            return last <= -1e8 ? -1.0 : (nowS - last) * 1000.0;
        }
    }

    /// <summary>One body as Subnautica draws it this frame.</summary>
    internal struct S15SnBodyView
    {
        public int Index;
        public bool Visible;
        /// <summary>The mapped direction (visibility, rows); <see cref="Place"/> = where the disc goes (the same unless S15.M3).</summary>
        public Vec3 Dir, Place;
        public double AngRadius, DistM;
    }

    /// <summary>The sky bodies of one frame (formats sky.disc, sky.size): the visible bodies from the frame and the list.</summary>
    internal static class S15SnSkyRules
    {
        /// <summary>A body is drawn: not the bound planet, the camera outside it, and some of it above the horizon.</summary>
        public static S15SnBodyView View(ref S15SkyFrame f, S15Body b, int index, double camY, bool sizeDiameter, bool unmapped = false)
        {
            var v = new S15SnBodyView { Index = index };
            if (b.Bound || !f.Valid) return v;
            Vec3 dir;
            double ang, dist;
            if (!S15Sky.View(ref f, b.Rel, b.RadiusM, out dir, out ang, out dist)) return v;
            if (sizeDiameter) ang = Math.Min(Math.PI / 2 - 1e-3, 2 * ang);
            v.Dir = dir;
            v.Place = dir;
            if (unmapped) { Vec3 d = b.Rel - f.CamRel; v.Place = d / d.Length; }   // S15.M3: the SE-world direction as if it were Subnautica's
            v.AngRadius = ang;
            v.DistM = dist;
            // the horizon's dip at camY above the sea (radius Rs ~ |camRel| - camY): a body entirely below it is under the sea
            double r = Math.Max(1.0, f.CamRel.Length - Math.Max(0.0, camY));
            double dip = Math.Acos(Math.Min(1.0, r / (r + Math.Max(0.0, camY))));
            v.Visible = Math.Asin(Math.Max(-1.0, Math.Min(1.0, dir.Y))) + ang > -dip;
            return v;
        }

        /// <summary>The disc's radius at distance <paramref name="d"/> for angular radius <paramref name="ang"/> (format sky.disc).</summary>
        public static double DiscRadius(double d, double ang) { return d * Math.Tan(ang); }

        /// <summary>The disc's distance for a camera far plane (format sky.disc).</summary>
        public static double DiscDistance(double far) { return far * S15Conventions.Sky.DiscDistancePermille / 1000.0; }

        /// <summary>The disc's colour channel (0..255) for a palette and limb channel, the light and the limb weight.</summary>
        public static byte Channel(byte palette, byte limb, double light, double limbWeight, bool atmosphere)
        {
            double c = palette * light;
            if (atmosphere) c = c * (1 - limbWeight) + limb * Math.Max(light, 0.35) * limbWeight;
            return (byte)Math.Max(0, Math.Min(255, (int)Math.Round(c)));
        }
    }
}
