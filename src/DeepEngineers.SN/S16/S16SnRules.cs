using System;
using DeepEngineers.Wire;

namespace DeepEngineers.SN
{
    // =================================================================================================================
 // S16 "Depth progression", Subnautica side: the Unity-free part (compiled into tests/DeepEngineers.SN.Tests). The SE
 // plugin decides every event (the creak cadence, the voice rule, the damage periods) and counts them in the C5
 // extension (CONTRACT §5.4 "S16 conventions", format c5.s16); Subnautica plays one sound per counter increment
 // (S16SeqReader: the first value is the baseline, several missed at once are one sound). S16.M7 (dev) plays the creaks
 // on Subnautica's own clock instead (every 0.5 s while the level is above 0): S16.SN red.
 // =================================================================================================================

 /// <summary>The S16 seeded failure of the Subnautica side.</summary>
    internal static class S16SnMutants
    {
        /// <summary>S16.M7: creaks every 0.5 s while the C5 creak level is above 0, not one per SE event (S16.SN red).</summary>
        public const string CreakClock = "S16.M7";
        /// <summary>S16.M11: the seated readout wraps (word wrapping on, a third of the compass width): the live TMP check, S16.SNHUD, goes red.</summary>
        public const string WrapReadout = "S16.M11";
        public static readonly string[] All = { CreakClock, WrapReadout };
    }

    /// <summary>What to play this frame.</summary>
    internal struct S16SnPlay
    {
        public bool Creak, Voice, Damage;
        public int CreakLevel;
        public uint CreakSeq, VoiceSeq, DamageSeq;
        public bool Any { get { return Creak || Voice || Damage; } }
    }

    /// <summary>The per-frame decision (Subnautica main thread): the C5 extension in, the sounds to play out.</summary>
    internal sealed class S16SnEvents
    {
        private readonly S16SeqReader _creak = new S16SeqReader(), _voice = new S16SeqReader(), _damage = new S16SeqReader();
        private double _nextMutantS;
        public long Frames, Absent, CreakEvents, VoiceEvents, DamageEvents;
        public long Coalesced { get { return _creak.Coalesced + _voice.Coalesced + _damage.Coalesced; } }

        public void Reset()
        {
            _creak.Reset();
            _voice.Reset();
            _damage.Reset();
            _nextMutantS = 0;
        }

        /// <summary>
 /// One frame with the latest C5 record (<paramref name="have"/> false: no record or no extension, nothing plays and
 /// the baselines are kept). <paramref name="nowS"/> is Subnautica's unscaled time (only S16.M7 uses it).
 /// </summary>
        public S16SnPlay Step(bool have, ref S16WireState w, double nowS, bool mutantClock)
        {
            var p = new S16SnPlay();
            Frames++;
            if (!have) { Absent++; return p; }
            p.CreakLevel = w.CreakLevel;
            p.CreakSeq = w.CreakSeq;
            p.VoiceSeq = w.VoiceSeq;
            p.DamageSeq = w.DamageSeq;
            int c = _creak.Step(w.WorldEpoch, w.CreakSeq);
            if (mutantClock)
            {
                c = 0;
                if (w.CreakLevel > 0 && nowS >= _nextMutantS) { c = 1; _nextMutantS = nowS + 0.5; }
            }
            if (c > 0) { p.Creak = true; CreakEvents++; }
            if (_voice.Step(w.WorldEpoch, w.VoiceSeq) > 0) { p.Voice = true; VoiceEvents++; }
            if (_damage.Step(w.WorldEpoch, w.DamageSeq) > 0) { p.Damage = true; DamageEvents++; }
            return p;
        }

        /// <summary>Volume of a creak by level (Subnautica's base crush sounds are already graded; louder past the limit).</summary>
        public static float CreakVolume(int level) { return level >= 3 ? 1f : level == 2 ? 0.85f : 0.7f; }
    }
}
