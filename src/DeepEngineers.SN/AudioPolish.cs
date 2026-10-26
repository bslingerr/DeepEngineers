using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S7 audio polish, Subnautica side (ARCHITECTURE §5.3 "Audio polish (S7)" A2, A3, A5, A6; CONTRACT §5.4 "S7
 /// conventions" <c>format audio.bus</c>; ROADMAP S7.N11). Nothing in Subnautica's mix changes (A2: the bus above water
 /// stays S2's, A3/A5: the ambience is the game's own through the ghost); this records it and enforces the event rule:
 /// <list type="bullet">
 /// <item><see cref="OnMix"/> (hook at the end of each <c>AudioMix.Tick</c>, every frame): the bus level, target and
 /// mute, and the <c>s7.audio</c> row {snFrame, bus {level, target, c5Target, muted}, events {type: started},
 /// refused {type: n}, above, biome, depth, underwater, ambience {total, playing, byInterior, names}}
 /// on every change and once per second while Subnautica is audible;</item>
 /// <item><see cref="OnEventSound"/>: the effects build pass sound player asks before it starts an S7 event sound
 /// (A2/A6: only for <c>Underwater</c> rows while <c>snBusTarget</c> &gt; 0; refused otherwise, counted), or reports a
 /// sound it started (<see cref="OnEventSoundStarted"/>: counted, and as <c>above</c> without <c>Underwater</c>).</item>
 /// </list>
 /// Game members: <c>Player.main</c>, <c>GetBiomeString</c> → string, <c>GetDepth</c> → float,
 /// <c>IsUnderwater</c> → bool
 /// <c>FMODGameParams.isPlaying</c> (bool), <c>interiorState</c> (<c>FMODGameParams.InteriorState</c>),
 /// <c>onlyInBiome</c> (string) (<c>FMODGameParams.cs</c>); <c>Object.FindObjectsOfType&lt;T&gt;</c> (Unity, every
 /// 30 s at most while rows are written). One instance per Subnautica process (the audio mix is the process's, not a
 /// character's: v3-safe). Every call is guarded: a failure turns the rows off (logged once), never the mix.
 /// </summary>
    internal static class AudioPolish
    {
        public static readonly EventSoundCounters Events = new EventSoundCounters();
        private static readonly AmbienceSummary Ambience = new AmbienceSummary();
        private static FMODGameParams[] _params;
        private static ulong _scanMs, _lastRowMs;
        private static int _lastKey = int.MinValue;
        private static float _level, _target, _c5Target;
        private static bool _muted, _failed;
        public static long Rows;

        /// <summary>The C5 bus target of the newest frame (A2: event sounds only while it is above 0).</summary>
        public static float BusTarget { get { return _c5Target; } }

        /// <summary>
 /// The effects build pass asks before starting an S7 event sound for a row with <paramref name="eventFlags"/>: true =
 /// start it (Underwater and the bus target above 0), false = lights only; counted per type either way.
 /// </summary>
        public static bool OnEventSound(byte type, byte eventFlags)
        {
            return Events.Count(type, eventFlags, _c5Target);
        }

        /// <summary>The effects build pass started a sound by its own rule: counted (and as <c>above</c> when the row had no Underwater flag).</summary>
        public static void OnEventSoundStarted(byte type, byte eventFlags)
        {
            Events.Started(type, eventFlags);
        }

        /// <summary>AudioMix.Tick (every frame, main thread): the bus as the mix set it; the <c>s7.audio</c> row when due.</summary>
        public static void OnMix(ulong nowMs, float level, float effectiveTarget, float c5Target, bool muted)
        {
            if (_failed) return;
            _level = level;
            _target = effectiveTarget;
            _c5Target = c5Target;
            _muted = muted;
            try { MaybeRow(nowMs); }
            catch (Exception e)
            {
                _failed = true;
                DeLog.Warn("DE.S7 audio rows OFF after " + e.GetType().Name + ": " + e.Message + " (the mix is untouched)");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void MaybeRow(ulong nowMs)
        {
            Player p = Player.main;
            bool underwater = p != null && p.IsUnderwater();
            int key = AudioPolishRules.StateKey(_target, _muted, false, underwater);
            bool changed = key != _lastKey;
            if (!AudioPolishRules.RowDue(changed, _target > 0f && !_muted, nowMs, _lastRowMs)) return;
            _lastKey = key;
            _lastRowMs = nowMs;
            if (_params == null || nowMs >= _scanMs + AudioPolishRules.AmbienceScanMs)
            {
                _scanMs = nowMs;
                _params = UnityEngine.Object.FindObjectsOfType<FMODGameParams>();
            }
            Ambience.Clear();
            if (_params != null)
                foreach (FMODGameParams g in _params)
                {
                    if (g == null) continue;
                    Ambience.Add(g.gameObject != null ? g.gameObject.name : null, (int)g.interiorState, g.isPlaying);
                }
            var started = new Dictionary<string, long>(StringComparer.Ordinal);
            var refused = new Dictionary<string, long>(StringComparer.Ordinal);
            Events.TakeWindow(started, refused);
            JsonOut j = S5Rows.Begin("s7.audio");
            j.Str("why", changed ? "change" : "periodic").Num("snFrame", (long)Time.frameCount);
            j.Obj("bus").Num("level", (double)_level).Num("target", (double)_target).Num("c5Target", (double)_c5Target).Bool("muted", _muted).EndObj();
            j.Obj("events");
            foreach (KeyValuePair<string, long> kv in started) j.Num(kv.Key, kv.Value);
            j.EndObj();
            j.Obj("refused");
            foreach (KeyValuePair<string, long> kv in refused) j.Num(kv.Key, kv.Value);
            j.EndObj();
            j.Num("above", Events.Above).Num("startedTotal", Events.TotalStarted).Num("refusedTotal", Events.TotalRefused);
            // review F18a: the requests by flag class (cumulative) and the violations, so S7.N11.bus and S7.N4.above can fail
            j.Num("requestsUnderwater", Events.RequestsUnderwater).Num("requestsDry", Events.RequestsDry)
             .Num("splashAllowed", Events.SplashAllowed).Num("allowedDry", Events.AllowedDry);
            j.Str("biome", p != null ? p.GetBiomeString() : null).Num("depth", p != null ? (double)p.GetDepth() : double.NaN)
             .Bool("underwater", underwater);
            j.Obj("ambience").Num("total", (long)Ambience.Total).Num("playing", (long)Ambience.Playing);
            j.Obj("byInterior");
            foreach (KeyValuePair<string, int> kv in Ambience.ByInterior) j.Num(kv.Key, (long)kv.Value);
            j.EndObj();
            j.Arr("names");
            foreach (string n in Ambience.Names) j.Str(null, n);
            j.EndArr().EndObj();
            S5Rows.End(j);
            Rows++;
        }

        public static string Describe()
        {
            return "DE.S7 audio bus " + _level.ToString("0.###") + " target " + _target.ToString("0.###") + (_muted ? " muted" : "") +
                   " events started " + Events.TotalStarted + " refused " + Events.TotalRefused + " above " + Events.Above + " rows " + Rows + (_failed ? " ROWS OFF" : "");
        }
    }
}
