using System;
using System.Collections.Generic;
using DeepEngineers.Wire;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The Unity-free half of S7's audio polish on the Subnautica side (ARCHITECTURE §5.3 "Audio polish (S7)" A2, A3, A5,
 /// A6; CONTRACT §5.4 "S7 conventions" <c>format audio.bus</c>; ROADMAP S7.N11), compiled into
 /// tests/DeepEngineers.SN.Tests:
 /// <list type="bullet">
 /// <item>A2/A6 the bus rule: an S7 event sound starts only for a row with <c>Underwater</c> while C5
 /// <c>snBusTarget</c> &gt; 0 (<see cref="EventSoundAllowed"/>); rows without it draw lights only; nothing beyond
 /// <c>EffectRadiusM</c> (250 m) of the camera;</item>
 /// <item>A3/A5 recorded, not changed: the bus level and target, the biome and depth the ghost feeds
 /// <c>FMODGameParams</c>, its playing ambience emitters by interior state (<see cref="AmbienceSummary"/>);</item>
 /// <item>the per-type counters of started and refused event sounds (<see cref="EventSoundCounters"/>) that the
 /// effects build pass player reports through <c>AudioPolish.OnEventSound</c>.</item>
 /// </list>
 /// </summary>
    internal static class AudioPolishRules
    {
        /// <summary>Rows <c>s7.audio</c>: every change of the bus state, else once per second while Subnautica is audible.</summary>
        public const ulong RowPeriodMs = 1000;
        /// <summary>The ambience emitter list is rebuilt this often (a scene scan) while rows are written.</summary>
        public const ulong AmbienceScanMs = 30000;
        /// <summary>At most this many ambience names in a row.</summary>
        public const int AmbienceNames = 12;

        /// <summary>A2/A6 (<c>format audio.bus</c>): an S7 event sound may start for this row.</summary>
        public static bool EventSoundAllowed(byte eventFlags, float snBusTarget)
        {
            return (eventFlags & S7Conventions.EventFlags.Underwater) != 0 && snBusTarget > 0f;
        }

        /// <summary>
 /// The rule with the orchestrator's documented exception: a surface splash
 /// (<c>Splash</c> with <c>Surface</c>) plays <c>Vehicle.splashSound</c> although its row is not Underwater; still
 /// only while Subnautica's bus is up (a muted bus would play it unheard).
 /// </summary>
        public static bool EventSoundAllowed(byte type, byte eventFlags, float snBusTarget)
        {
            return EventSoundAllowed(eventFlags, snBusTarget) || SplashException(type, eventFlags, snBusTarget);
        }

        /// <summary>The splash exception to <c>format audio.bus</c> (CONTRACT §5.4 "S7 conventions", prose).</summary>
        public static bool SplashException(byte type, byte eventFlags, float snBusTarget)
        {
            return type == S7Conventions.EventType.Splash && (eventFlags & S7Conventions.EventFlags.Surface) != 0 && snBusTarget > 0f;
        }

        /// <summary>A6: and only within the effect radius of the camera (metres).</summary>
        public static bool EventSoundInRange(double distanceM)
        {
            return !double.IsNaN(distanceM) && distanceM <= S7Conventions.EventLimits.EffectRadiusM;
        }

        /// <summary>The bus state a row reports; a change writes a row at once (level quantised to 0.05).</summary>
        public static int StateKey(float target, bool muted, bool insideWalkable, bool underwater)
        {
            int lvl = (int)Math.Round(Math.Max(0f, Math.Min(1f, target)) * 20f);
            return lvl | (muted ? 64 : 0) | (insideWalkable ? 128 : 0) | (underwater ? 256 : 0);
        }

        public static bool RowDue(bool changed, bool audible, ulong nowMs, ulong lastRowMs)
        {
            if (changed) return true;
            return audible && nowMs >= lastRowMs + RowPeriodMs;
        }

        /// <summary>The FMODGameParams interior states, by value.</summary>
        public static readonly string[] InteriorStates = { "Always", "OnlyOutside", "OnlyInside", "OnlyInSubOrBase", "OnlyInSub", "OnlyInBase" };

        public static string InteriorName(int state)
        {
            return state >= 0 && state < InteriorStates.Length ? InteriorStates[state] : "state" + state;
        }
    }

    /// <summary>Started and refused S7 event sounds per event type (1..13), since the session start and since the last row.</summary>
    internal sealed class EventSoundCounters
    {
        public const int Types = 14;
        private readonly long[] _started = new long[Types], _refused = new long[Types], _rowStarted = new long[Types], _rowRefused = new long[Types];
        private readonly object _lock = new object();

        /// <summary>
 /// One event sound the player asked for: started when <see cref="AudioPolishRules.EventSoundAllowed(byte, byte, float)"/>,
 /// refused (counted) otherwise. Review F18a: every request is also counted by flag class, so the
 /// oracle sees the rule exercised (requests without Underwater) and catches any allowed one that is not the splash
 /// exception (<see cref="AllowedDry"/>, 0 in a passing run).
 /// </summary>
        public bool Count(byte type, byte eventFlags, float snBusTarget)
        {
            bool ok = AudioPolishRules.EventSoundAllowed(type, eventFlags, snBusTarget);
            return Record(type, eventFlags, snBusTarget, ok);
        }

        /// <summary>Counts one request whose answer is <paramref name="allowed"/> (the rule's, or a caller's own: tests feed a broken one).</summary>
        public bool Record(byte type, byte eventFlags, float snBusTarget, bool allowed)
        {
            int t = type < Types ? type : 0;
            bool under = (eventFlags & S7Conventions.EventFlags.Underwater) != 0;
            lock (_lock)
            {
                if (allowed) _started[t]++;
                else _refused[t]++;
                if (under) RequestsUnderwater++;
                else
                {
                    RequestsDry++;
                    if (allowed)
                    {
                        if (AudioPolishRules.SplashException(type, eventFlags, snBusTarget)) SplashAllowed++;
                        else AllowedDry++;
                    }
                }
            }
            return allowed;
        }

        /// <summary>Review F18a: requests by flag class, the splash exceptions, and the violations (allowed without Underwater).</summary>
        public long RequestsUnderwater, RequestsDry, SplashAllowed, AllowedDry;

        /// <summary>A sound the effects build pass started without asking (its own rule): counted as started, and as "above" when its row had no Underwater flag.</summary>
        public void Started(byte type, byte eventFlags)
        {
            int t = type < Types ? type : 0;
            lock (_lock)
            {
                _started[t]++;
                if ((eventFlags & S7Conventions.EventFlags.Underwater) == 0) Above++;
            }
        }

        /// <summary>Event sounds started for rows without <c>Underwater</c> (A2 violations; 0 in a passing run).</summary>
        public long Above;

        /// <summary>The counts since the last call, by type name (only non-zero ones), then the window restarts.</summary>
        public void TakeWindow(Dictionary<string, long> started, Dictionary<string, long> refused)
        {
            lock (_lock)
            {
                for (int t = 0; t < Types; t++)
                {
                    long s = _started[t] - _rowStarted[t], r = _refused[t] - _rowRefused[t];
                    string name = t == 0 ? "other" : S7Conventions.EventType.Name(t);
                    if (s != 0 && started != null) started[name] = s;
                    if (r != 0 && refused != null) refused[name] = r;
                    _rowStarted[t] = _started[t];
                    _rowRefused[t] = _refused[t];
                }
            }
        }

        public long TotalStarted { get { lock (_lock) { long n = 0; foreach (long v in _started) n += v; return n; } } }
        public long TotalRefused { get { lock (_lock) { long n = 0; foreach (long v in _refused) n += v; return n; } } }
    }

    /// <summary>The playing <c>FMODGameParams</c> ambience emitters of one sample (A3/A5, information only).</summary>
    internal sealed class AmbienceSummary
    {
        public int Total, Playing;
        public readonly Dictionary<string, int> ByInterior = new Dictionary<string, int>(StringComparer.Ordinal);
        public readonly List<string> Names = new List<string>();

        public void Clear()
        {
            Total = Playing = 0;
            ByInterior.Clear();
            Names.Clear();
        }

        public void Add(string name, int interiorState, bool playing)
        {
            Total++;
            if (!playing) return;
            Playing++;
            string k = AudioPolishRules.InteriorName(interiorState);
            int n;
            ByInterior.TryGetValue(k, out n);
            ByInterior[k] = n + 1;
            if (Names.Count < AudioPolishRules.AmbienceNames && name != null) Names.Add(name);
        }
    }
}
