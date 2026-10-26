using System;
using System.Threading;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>
 /// `sePhase` bookkeeping (CONTRACT §5.4 C0, §7; ARCHITECTURE §5.14). The Harmony patches only flip flags here:
 /// LoadingWorld from the `MyGuiScreenLoading.RunLoad` prefix (before any blocking work), Saving between the
 /// `MySession.Save(out MySessionSnapshot, …)` prefix and its finalizer, Exiting from the `MySandboxGame.ExitThreadSafe`
 /// prefix (latched). InWorld / Menu are derived every tick from the session and the gameplay screen.
 /// Game-agnostic: the game facts are passed to <see cref="Compute"/>.
 /// </summary>
    internal static class Phases
    {
        private static int _savingDepth;
        private static volatile bool _exiting;
        private static volatile bool _loading;
        private static volatile bool _runLoadEnded;
        private static volatile bool _loadIsGameplay;
        private static int _worldEpoch;
        private static ulong _loadStartMs;
        private static string _loadTarget = "";
        private static long _loadStartTimestamp, _runLoadEndTimestamp;

        /// <summary>Raised (on the patching thread) when a prefix changes the phase, so the link writes it at once.</summary>
        public static Action<uint> PhaseForced;

        /// <summary>CONTRACT §7 "World load": `worldEpoch`++ at every world load (RunLoad of a gameplay screen).</summary>
        public static uint WorldEpoch { get { return (uint)Volatile.Read(ref _worldEpoch); } }
        public static bool Exiting { get { return _exiting; } }
        public static bool Saving { get { return Volatile.Read(ref _savingDepth) > 0; } }
        public static bool Loading { get { return _loading; } }
        public static string LoadTarget { get { return _loadTarget; } }
        /// <summary>World-load timing (S1.N12): Stopwatch timestamp of the last RunLoad prefix (0 before any load).</summary>
        public static long LoadStartTimestamp { get { return Interlocked.Read(ref _loadStartTimestamp); } }
        /// <summary>Stopwatch timestamp of the last RunLoad finalizer (0 while a load runs or before any load).</summary>
        public static long RunLoadEndTimestamp { get { return Interlocked.Read(ref _runLoadEndTimestamp); } }
        /// <summary>True when the last RunLoad was a gameplay (world) load.</summary>
        public static bool LoadIsGameplay { get { return _loadIsGameplay; } }

        /// <summary>Safety net: a load whose screen never reports done stops counting as loading after this long.</summary>
        public const ulong MaxLoadMs = 30 * 60 * 1000;

        public static void OnRunLoadStart(bool gameplay, string target, ulong nowMs)
        {
            _loadTarget = target ?? "";
            _loadIsGameplay = gameplay;
            _runLoadEnded = false;
            _loadStartMs = nowMs;
            Interlocked.Exchange(ref _loadStartTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
            Interlocked.Exchange(ref _runLoadEndTimestamp, 0);
            _loading = true;
            if (gameplay) Interlocked.Increment(ref _worldEpoch);
            Raise(SePhase.LoadingWorld);
        }

        public static void OnRunLoadEnd()
        {
            Interlocked.Exchange(ref _runLoadEndTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
            _runLoadEnded = true;
        }

        public static void OnSaveEnter()
        {
            Interlocked.Increment(ref _savingDepth);
            if (!_exiting) Raise(SePhase.Saving);
        }

        public static void OnSaveExit(uint phaseAfter)
        {
            int d = Interlocked.Decrement(ref _savingDepth);
            if (d < 0) Interlocked.Exchange(ref _savingDepth, 0);
            if (d <= 0 && !_exiting) Raise(phaseAfter);
        }

        public static void OnExit()
        {
            _exiting = true;
            Raise(SePhase.Exiting);
        }

        private static void Raise(uint phase)
        {
            Action<uint> a = PhaseForced;
            if (a == null) return;
            try { a(phase); } catch (Exception) { }
        }

        /// <summary>
 /// The phase for this tick (update thread). Clears the loading flag once the gameplay screen reports its load
 /// done, when a non-gameplay load (e.g. the main-menu inventory scene) has run, or when a failed load left no
 /// session.
 /// </summary>
        public static uint Compute(bool sessionExists, bool gameplayScreen, bool gameplayLoadingDone, ulong nowMs)
        {
            if (_exiting) return SePhase.Exiting;
            if (Volatile.Read(ref _savingDepth) > 0) return SePhase.Saving;
            if (_loading)
            {
                if (!_runLoadEnded) return SePhase.LoadingWorld;
                bool done;
                if (!_loadIsGameplay) done = true;
                else if (sessionExists && gameplayScreen && gameplayLoadingDone) done = true;
                else if (!sessionExists) done = true; // failed load: back at the menu
                else done = nowMs - _loadStartMs > MaxLoadMs;
                if (!done) return SePhase.LoadingWorld;
                _loading = false;
            }
            return sessionExists && gameplayScreen ? SePhase.InWorld : SePhase.Menu;
        }
    }
}
