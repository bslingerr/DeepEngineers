using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace DeepEngineers.Terrain
{
    /// <summary>
 /// Background first touch of the batch files under the tiles. Without it the first
 /// read of a batch maps the file and walks its 125 node counts (which touches nearly every page of an average file)
 /// on whichever thread asks first, possibly a blocking shape request inside SE's Havok step
 /// One background thread (below-normal priority, Windows background mode: very low I/O priority) indexes
 /// the planned slots in the given order (nearest first) through <see cref="SnOctreeSource.Prefetch"/> and, with
 /// <c>fault</c>, touches every page of each mapped file; then it waits for and runs the I/O retries of the batches
 /// that failed (finding 3), until each one is data or bad. Readers that reach a batch first index it themselves, as
 /// before (the stripe lock makes the two meet). It holds no reference on the source: once the source is closing
 /// <see cref="SnOctreeSource.Prefetch"/> answers −1 and the thread ends; <see cref="Cancel"/> ends it between batches.
 /// Logs <c>DE.Terrain PREFETCH start|done|cancelled|failed</c> through <see cref="TerrainLog"/>.
 /// </summary>
    public sealed class SnPrefetcher
    {
        public const int StateCreated = 0, StatePlanning = 1, StateRunning = 2, StateRetrying = 3, StateDone = 4, StateCancelled = 5, StateFailed = 6;
        private static readonly string[] StateNames = { "created", "planning", "running", "retrying", "done", "cancelled", "failed" };

        private readonly SnOctreeSource _src;
        private readonly Func<int[]> _plan;
        private readonly bool _fault, _lowPriority;
        private readonly ManualResetEvent _finished = new ManualResetEvent(false);
        private volatile bool _cancel;
        private volatile int _state;
        private long _planned, _done, _data, _missing, _bad, _ioRetried, _pages;
        private long _t0, _t1;
        private volatile string _error;

        private SnPrefetcher(SnOctreeSource src, Func<int[]> plan, bool fault, bool lowPriority)
        {
            _src = src;
            _plan = plan;
            _fault = fault;
            _lowPriority = lowPriority;
        }

        /// <summary>
 /// Starts the thread. <paramref name="plan"/> runs on it and returns the batch slots to prefetch, nearest first
 /// (<see cref="SnOctreeFormat.BatchSlot"/>); an exception there ends the prefetch (state failed, logged).
 /// </summary>
        public static SnPrefetcher Start(SnOctreeSource src, Func<int[]> plan, bool fault, bool lowPriority)
        {
            if (src == null) throw new ArgumentNullException("src");
            if (plan == null) throw new ArgumentNullException("plan");
            var p = new SnPrefetcher(src, plan, fault, lowPriority);
            var t = new Thread(p.Run) { IsBackground = true, Name = "DE.Terrain prefetch", Priority = lowPriority ? ThreadPriority.BelowNormal : ThreadPriority.Normal };
            t.Start();
            return p;
        }

        /// <summary>Asks the thread to stop after the batch in hand (never blocks).</summary>
        public void Cancel() { _cancel = true; }

        /// <summary>Waits until the thread ended (tests); true when it did within <paramref name="ms"/>.</summary>
        public bool Wait(int ms) { return _finished.WaitOne(ms); }

        public int State { get { return _state; } }
        public string StateName { get { return StateNames[_state]; } }
        public bool Finished { get { int s = _state; return s >= StateDone; } }
        public long Planned { get { return Interlocked.Read(ref _planned); } }
        public long Done { get { return Interlocked.Read(ref _done); } }
        public long Data { get { return Interlocked.Read(ref _data); } }
        public long Missing { get { return Interlocked.Read(ref _missing); } }
        public long Bad { get { return Interlocked.Read(ref _bad); } }
        /// <summary>Retry attempts this thread made on batches waiting for an I/O retry.</summary>
        public long IoRetried { get { return Interlocked.Read(ref _ioRetried); } }
        public long PagesTouched { get { return Interlocked.Read(ref _pages); } }
        public string Error { get { return _error; } }

        /// <summary>Milliseconds since the start (until the end once finished).</summary>
        public double ElapsedMs
        {
            get
            {
                long t0 = Interlocked.Read(ref _t0);
                if (t0 == 0) return 0;
                long t1 = Interlocked.Read(ref _t1);
                return ((t1 != 0 ? t1 : Stopwatch.GetTimestamp()) - t0) * 1000.0 / Stopwatch.Frequency;
            }
        }

        public string Text
        {
            get
            {
                return StateName + " " + Done + "/" + Planned + " batches (data " + Data + ", missing " + Missing + ", bad " + Bad + ", io retries " + IoRetried +
                       ", pages " + PagesTouched + ") " + ElapsedMs.ToString("0", CultureInfo.InvariantCulture) + " ms" + (_error != null ? " error " + _error : "");
            }
        }

        private void Run()
        {
            Interlocked.Exchange(ref _t0, Stopwatch.GetTimestamp());
            bool background = false;
            try
            {
                if (_lowPriority) background = Background.Enter();
                _background = background;
                _state = StatePlanning;
                int[] slots = _plan() ?? new int[0];
                Interlocked.Exchange(ref _planned, slots.Length);
                TerrainLog.Write("DE.Terrain PREFETCH start batches=" + slots.Length + " fault=" + _fault + " backgroundMode=" + background +
                                 " planMs=" + ElapsedMs.ToString("0", CultureInfo.InvariantCulture) + " (one thread, nearest first; readers that come first index a batch themselves)");
                _state = StateRunning;
                bool closing = false;
                foreach (int slot in slots)
                {
                    if (_cancel) break;
                    long pages;
                    int r = _background ? _src.Prefetch(slot, _fault, out pages, _normalIo, _backgroundIo) : _src.Prefetch(slot, _fault, out pages);
                    if (r < 0) { closing = true; break; }
                    Count(r, pages);
                    Interlocked.Increment(ref _done);
                }
                if (!_cancel && !closing) closing = !RetryPass(slots);
                Interlocked.Exchange(ref _t1, Stopwatch.GetTimestamp());
                _state = _cancel || closing ? StateCancelled : StateDone;
                TerrainLog.Write("DE.Terrain PREFETCH " + (_state == StateDone ? "done " : "cancelled ") + Text);
            }
            catch (Exception e)
            {
                _error = e.GetType().Name + ": " + e.Message;
                Interlocked.Exchange(ref _t1, Stopwatch.GetTimestamp());
                _state = StateFailed;
                TerrainLog.Write("DE.Terrain PREFETCH failed " + Text);
            }
            finally
            {
                if (background) Background.Leave();
                _finished.Set();
            }
        }

        //: the indexing (under the batch's stripe lock) at normal I/O priority, the page touching in background mode
        private volatile bool _background;
        private static readonly Action _normalIo = () => Background.Leave();
        private static readonly Action _backgroundIo = () => Background.Enter();

        private void Count(int r, long pages)
        {
            Interlocked.Add(ref _pages, pages);
            if (r == SnOctreeSource.StateData) Interlocked.Increment(ref _data);
            else if (r == SnOctreeSource.StateMissing) Interlocked.Increment(ref _missing);
            else if (r == SnOctreeSource.StateBad) Interlocked.Increment(ref _bad);
        }

        /// <summary>Retries the planned batches that wait for an I/O retry until none waits. False when the source closed.</summary>
        private bool RetryPass(int[] slots)
        {
            _state = StateRetrying;
            while (!_cancel)
            {
                long next = long.MaxValue;
                foreach (int slot in slots)
                    if (_src.ReadyOf(slot) == SnOctreeSource.StateIoRetry) next = Math.Min(next, _src.RetryAtTicks(slot));
                if (next == long.MaxValue) return true;
                long waitMs = (next - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
                if (waitMs > 0)
                {
                    Thread.Sleep((int)Math.Min(waitMs, 100));
                    continue;
                }
                foreach (int slot in slots)
                {
                    if (_cancel) return true;
                    if (_src.ReadyOf(slot) != SnOctreeSource.StateIoRetry) continue;
                    long pages;
                    int r = _background ? _src.Prefetch(slot, _fault, out pages, _normalIo, _backgroundIo) : _src.Prefetch(slot, _fault, out pages);
                    if (r < 0) return false;
                    Interlocked.Increment(ref _ioRetried);
                    if (r != SnOctreeSource.StateIoRetry) Count(r, pages);
                }
            }
            return true;
        }

        /// <summary>Windows thread background mode (<c>SetThreadPriority(GetCurrentThread, THREAD_MODE_BACKGROUND_BEGIN)</c>): lower CPU, I/O and memory priority.</summary>
        private static class Background
        {
            private const int ModeBegin = 0x00010000, ModeEnd = 0x00020000;

            [DllImport("kernel32.dll")]
            private static extern IntPtr GetCurrentThread();

            [DllImport("kernel32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool SetThreadPriority(IntPtr thread, int priority);

            public static bool Enter()
            {
                try { return SetThreadPriority(GetCurrentThread(), ModeBegin); }
                catch (Exception) { return false; }
            }

            public static void Leave()
            {
                try { SetThreadPriority(GetCurrentThread(), ModeEnd); }
                catch (Exception) { }
            }
        }
    }
}
