using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The 1-second plugin log of ROADMAP S0.N7 (one JSON object per line in
 /// `%LOCALAPPDATA%\DeepEngineers\logs\se-&lt;timestamp&gt;.jsonl`, `"ev":"sample"`). `frameTimeAvgMs` is SE's own
 /// `MyFpsManager.FrameTimeAvg` (update + render time averaged over the last 60 frames; the "FrameTime" column of
 /// SE's STATISTICS log line), so a plugin-less baseline can be compared with SE's own statistics; `pluginUs*` is
 /// the time spent in our plugin Update (Stopwatch). One string is built per second; nothing per frame.
 /// </summary>
    internal sealed class PerfLog
    {
        private static readonly double TicksToUs = 1e6 / Stopwatch.Frequency;

        private readonly StringBuilder _sb = new StringBuilder(768);
        private readonly long _startTimestamp;
        private ulong _nextMs;
        private long _seq;
        private long _updates;
        private long _sumTicks;
        private long _maxTicks;

        public PerfLog(long startTimestamp)
        {
            _startTimestamp = startTimestamp;
        }

        /// <summary>Plugin Update cost (Stopwatch ticks), every tick.</summary>
        public void AddUpdate(long elapsedTicks)
        {
            _updates++;
            _sumTicks += elapsedTicks;
            if (elapsedTicks > _maxTicks) _maxTicks = elapsedTicks;
        }

        public bool Due(ulong nowMs)
        {
            if (_nextMs == 0) { _nextMs = nowMs + 1000; return false; }
            return nowMs >= _nextMs;
        }

        /// <summary>Writes one sample (call when <see cref="Due"/>). Update thread.</summary>
        public void Write(ulong nowMs, uint phase, Link link, uint worldEpoch, uint helloEpoch, bool paused, bool foreground, ulong gameplayTick)
        {
            _nextMs = nowMs + 1000;
            _seq++;
            double meanUs = _updates > 0 ? _sumTicks * TicksToUs / _updates : 0;
            double maxUs = _maxTicks * TicksToUs;
            long updates = _updates;
            _updates = 0; _sumTicks = 0; _maxTicks = 0;

            StringBuilder sb = _sb;
            sb.Length = 0;
            sb.Append("{\"t\":");
            JsonText.AppendString(sb, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            sb.Append(",\"ev\":\"sample\",\"seq\":").Append(_seq);
            sb.Append(",\"s\":");
            JsonText.AppendNumber(sb, Math.Round((Stopwatch.GetTimestamp() - _startTimestamp) / (double)Stopwatch.Frequency, 3));
            sb.Append(",\"phase\":");
            JsonText.AppendString(sb, SePhase.Name(phase));
            sb.Append(",\"worldEpoch\":").Append(worldEpoch);
            sb.Append(",\"helloEpoch\":").Append(helloEpoch);
            sb.Append(",\"gameplayTick\":").Append(gameplayTick);
            sb.Append(",\"paused\":").Append(paused ? "true" : "false");
            sb.Append(",\"foreground\":").Append(foreground ? "true" : "false");
            sb.Append(",\"gate\":");
            JsonText.AppendString(sb, ActivationGate.State.ToString());
            sb.Append(",\"version\":");
            JsonText.AppendString(sb, VersionGate.Status.ToString());
            sb.Append(",\"link\":");
            JsonText.AppendString(sb, link != null ? link.State.ToString() : "None");
            if (link != null && link.State == LinkState.Up)
            {
                sb.Append(",\"peer\":");
                JsonText.AppendString(sb, link.Peer.ToString());
                sb.Append(",\"seGen\":").Append(link.SeGeneration);
                sb.Append(",\"snPid\":").Append(link.LastSnPid);
                sb.Append(",\"snGen\":").Append(link.LastSnGeneration);
                sb.Append(",\"snPhase\":");
                JsonText.AppendString(sb, SnPhase.Name(link.LastSnPhase));
                sb.Append(",\"snHeartbeatAgeMs\":").Append(link.LastSnHeartbeatAgeMs);
                sb.Append(",\"linkEpoch\":").Append(link.LinkEpoch);
                sb.Append(",\"relay\":").Append(link.RelayHeld ? "true" : "false");
            }
            sb.Append(",\"frameTimeAvgMs\":");
            JsonText.AppendNumber(sb, Round(GameState.FrameTimeAvgMs));
            sb.Append(",\"frameTimeLastMs\":");
            JsonText.AppendNumber(sb, Round(GameState.FrameTimeMs));
            sb.Append(",\"frameTimeMinMs\":");
            JsonText.AppendNumber(sb, Round(GameState.FrameTimeMinMs));
            sb.Append(",\"frameTimeMaxMs\":");
            JsonText.AppendNumber(sb, Round(GameState.FrameTimeMaxMs));
            sb.Append(",\"fps\":").Append(GameState.Fps);
            sb.Append(",\"cpuLoadSmooth\":");
            JsonText.AppendNumber(sb, Round(GameState.CpuLoadSmooth));
            sb.Append(",\"updates\":").Append(updates);
            sb.Append(",\"pluginUsMean\":");
            JsonText.AppendNumber(sb, Math.Round(meanUs, 2));
            sb.Append(",\"pluginUsMax\":");
            JsonText.AppendNumber(sb, Math.Round(maxUs, 2));
            S8Memory.Append(sb); // S8-memory (ROADMAP S8.N3): workingSetMB, privateMB, gcHeapMB, gc0-2, memoryLoadPct, commitPct
            sb.Append('}');
            Jsonl.WriteLine(sb.ToString());
        }

        private static double Round(float v)
        {
            return float.IsNaN(v) || float.IsInfinity(v) ? double.NaN : Math.Round(v, 4);
        }
    }
}
