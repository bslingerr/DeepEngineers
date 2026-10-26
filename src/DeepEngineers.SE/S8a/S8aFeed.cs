using System;
using System.Collections.Generic;
using DeepEngineers.SE.Terrain;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using C2S8a = DeepEngineers.Wire.S8aConventions.C2S8a;
using C2S8aFlags = DeepEngineers.Wire.S8aConventions.C2S8aFlags;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The SE side of the S8a change feed (CONTRACT §5.4 "S8a conventions": the C2 S8a header at region offset 0x50000
 /// and the notice rows, written inside the C2 seqlock; the C7 S8a block read each tick; formats feed.ack,
 /// feed.resync). Wraps Wire's <see cref="S8aFeedWriter"/>: <see cref="Enqueue"/> from any thread (the drill converter,
 /// later the window worker), <see cref="WriteInto"/> from <c>Link.WriteSeEntities</c> through <c>Link.C2Extra</c> (the
 /// B2 hook: the same seqlock window as the entity tables), <see cref="OnC7"/> from the host's tick with the latest
 /// <c>SnStatus</c>. A new stream per world (the seq restarts at 1; the stream id changes); rows only while
 /// Subnautica's C7 block says version 1. While S8a is off the header is zeroed once (version 0: Subnautica ignores the
 /// block). Rows: <c>s8a.notice</c> per queued notice, <c>s8a.feed</c> at 1 Hz and on change.
 /// </summary>
    internal static unsafe class S8aFeed
    {
        private static readonly object Lock = new object();
        private static S8aFeedWriter _w;
        private static uint _stream;
        private static uint _streamSeed;
        private static S8aC7 _c7;
        private static bool _hasC7;
        private static byte _c7Version;
        private static bool _offWritten;
        private static long _notices, _writes, _enqueueFailed;
        private static ulong _nextRowMs;
        private static uint _rowAck, _rowApplied, _rowFirst;
        private static int _rowPending, _rowRows;
        private static int _lastRows;

        /// <summary>The header state the host keeps current (flags, storeKey, overlayGen, chartSeq, cameraBiome, near*, seabedBelowCamM, windowGen, drillGen, chartKey).</summary>
        public static S8aHeader State;

        public static uint Stream { get { return _stream; } }
        public static bool Active { get { return _w != null; } }
        /// <summary>C7 <c>appliedSeq</c> of the current stream (0 until Subnautica answers).</summary>
        public static uint AppliedSeq { get { lock (Lock) return _hasC7 && _c7.version == 1 && _c7.stream == _stream ? _c7.appliedSeq : 0u; } }
        public static uint AckSeq { get { lock (Lock) return _hasC7 && _c7.version == 1 && _c7.stream == _stream ? _c7.ackSeq : 0u; } }
        public static bool SnHasS8a { get { return _c7Version == 1; } }
        public static S8aC7 LastC7 { get { lock (Lock) return _c7; } }
        public static bool HasC7 { get { return _hasC7; } }

        public static void Init()
        {
            _streamSeed = (uint)(Win32.TickCount64() & 0x7FFFFFFF) | 1u;
            State = new S8aHeader { cameraBiome = S8aMaterialMap.BiomeNone, seabedBelowCamM = float.NaN };
        }

        private static uint _resetGen;

        /// <summary>
 /// A new world (or a new link epoch): seq restarts at 1 under a new stream id; the queue is dropped. The first notice
 /// of the stream is <c>StoreReset(Drill)</c> (CONTRACT §3.3 fail-safe (6), ): Subnautica replaces its
 /// view with the manifest and reloads every loaded batch whose served version changed or disappeared.
 /// </summary>
        public static void NewStream(string why)
        {
            lock (Lock)
            {
                _stream = unchecked(_streamSeed + (uint)(Phases.WorldEpoch * 7919u) + (uint)_writes % 1000u);
                if (_stream == 0) _stream = 1;
                if (_w == null) _w = new S8aFeedWriter(_stream);
                else _w.Restart(_stream, false);
                _hasC7 = false;
                _resetGen++;
            }
            uint seq = Enqueue(S8aNotice.StoreReset(S8aConventions.NoticeLayer.Drill, _resetGen));
            DeLog.Info("DE.S8a feed stream " + _stream + " (" + why + "): StoreReset drill gen " + _resetGen + " queued as seq " + seq);
        }

        public static void Close(string why)
        {
            lock (Lock)
            {
                if (_w == null) return;
                _w = null;
                _hasC7 = false;
            }
            DeLog.Info("DE.S8a feed closed (" + why + ")");
        }

        /// <summary>Queues a notice (any thread); returns its seq (0 when the feed is not up or the notice is malformed).</summary>
        public static uint Enqueue(in S8aNotice n)
        {
            S8aFeedWriter w = _w;
            if (w == null) { _enqueueFailed++; return 0; }
            uint seq;
            if (!w.TryEnqueue(n, out seq)) { _enqueueFailed++; return 0; }
            _notices++;
            Jsonl.WriteLine(S4Rows.Row("s8a.notice", "tick", (long)GameState.GameplayTick, "seq", (long)seq, "kind", n.kind, "layer", n.layer, "flags", n.flags,
                                       "bx", n.bx, "by", n.by, "bz", n.bz, "version", (long)n.version, "min", new object[] { n.minX, n.minY, n.minZ },
                                       "max", new object[] { n.maxX, n.maxY, n.maxZ }, "octrees", n.octrees, "cells", (long)n.cells, "pending", w.Pending, "stream", (long)w.Stream));
            return seq;
        }

        /// <summary>
 /// <c>Link.C2Extra</c>: inside the C2 seqlock write of every tick. Header version 1 with the host's state while S8a is
 /// on (rows only while C7 says version 1); the header zeroed once when S8a is off. Never throws.
 /// </summary>
        public static void WriteInto(byte* c2Region, ulong tick)
        {
            try
            {
                S8aFeedWriter w = _w;
                if (w == null || !S8aOverlay.Enabled)
                {
                    if (!_offWritten) { S8aHeader.WriteOff(c2Region); _offWritten = true; }
                    return;
                }
                _offWritten = false;
                S8aHeader st = State;
                byte flags = 0;
                if (S8aOverlay.DrillLayerOn) flags |= C2S8aFlags.DrillOn;
                if (S8aWindow.WindowOn) flags |= C2S8aFlags.WindowOn;
                if (S8aChartManager.TreadmillOn) flags |= C2S8aFlags.TreadmillOn;
                if (S8aChartManager.ChartIndex >= 1) flags |= C2S8aFlags.OffSite;     // terrain 4 "chart.offsite"
                if (S8aWindow.NearPending) flags |= C2S8aFlags.NearPending;
                if (S8aWindow.NearEmpty) flags |= C2S8aFlags.NearEmpty;
                st.flags = flags;
                st.storeKey = S8aOverlay.StoreKey != 0 ? S8aOverlay.StoreKey : S8aHost.StoreKey;
                st.overlayGen = S8aOverlay.OverlayGen;
                st.drillGen = S8aDrillConverter.DrillGen;
                st.chartSeq = S8aChartManager.ChartSeq;
                st.chartKey = S8aChartManager.ChartKey;
                st.cameraBiome = S8aWindow.CameraBiome;
                st.nearPendingCount = S8aWindow.NearPendingCount;
                st.seabedBelowCamM = S8aWindow.SeabedBelowCamM;
                st.windowGen = S8aWindow.WindowGen;
                int rows = w.WriteInto(c2Region, tick, st, _c7Version == 1);
                _lastRows = rows;
                _writes++;
            }
            catch (Exception) { }
        }

        /// <summary>The host's tick: the C7 S8a block of the latest <c>SnStatus</c> (acks, the resync echo, the version).</summary>
 /// <summary>Subnautica's own C7 <c>areaBuilt</c> flag and presented SE tick as last read.</summary>
        public static bool SnAreaBuilt { get; private set; }
        public static ulong SnPresentedTick { get; private set; }

        public static void OnC7(SnStatus s)
        {
            S8aC7 c = S8aC7.ReadFrom(ref s);
            SnAreaBuilt = (s.flags & SnStatusFlags.AreaBuilt) != 0;
            SnPresentedTick = s.presentedSeTick;
            lock (Lock)
            {
                _c7 = c;
                _hasC7 = true;
                _c7Version = c.version;
                if (c.version == 1 && _w != null) _w.OnAck(c.stream, c.ackSeq, c.resyncEpoch);
            }
        }

        private static uint _flushedResync;

        /// <summary>1 Hz and on change: <c>s8a.feed</c> {tick, firstSeq, rows, pending, dropped, ackSeq, appliedSeq, resyncEpoch}; a new resync flushes both manifests at once (Subnautica re-reads them).</summary>
        public static void Rows(ulong nowMs)
        {
            S8aFeedWriter w = _w;
            if (w == null) return;
            if (w.ResyncPending && w.ResyncEpoch != _flushedResync)
            {
                _flushedResync = w.ResyncEpoch;
                S8aDrillConverter.FlushManifestNow();
                S8aWindow.FlushManifestNow();
            }
            uint ack = AckSeq, applied = AppliedSeq, first = w.FirstSeq;
            int pending = w.Pending, rows = _lastRows;
            bool changed = ack != _rowAck || applied != _rowApplied || first != _rowFirst || pending != _rowPending || rows != _rowRows;
            if (!changed && nowMs < _nextRowMs) return;
            _nextRowMs = nowMs + 1000;
            _rowAck = ack; _rowApplied = applied; _rowFirst = first; _rowPending = pending; _rowRows = rows;
            S8aC7 c = LastC7;
            Jsonl.WriteLine(S4Rows.Row("s8a.feed", "tick", (long)GameState.GameplayTick, "stream", (long)w.Stream, "firstSeq", (long)first, "rows", rows, "pending", pending,
                                       "dropped", (long)w.Dropped, "ackSeq", (long)ack, "appliedSeq", (long)applied, "resyncEpoch", (long)w.ResyncEpoch,
                                       "resyncPending", w.ResyncPending, "snVersion", _c7Version, "snFlags", c.flags, "reloadsPending", c.reloadsPending,
                                       "batchesServed", (long)c.batchesServed, "reloadFailures", (long)c.reloadFailures, "lastLatencyMs", c.lastLatencyMs,
                                       "snStoreKey", S8aStore.Hex16(c.storeKey), "notices", _notices, "writes", _writes));
        }

        public static Dictionary<string, object> Status()
        {
            S8aFeedWriter w = _w;
            S8aC7 c = LastC7;
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "active", w != null }, { "stream", (long)_stream }, { "pending", w != null ? w.Pending : 0 }, { "firstSeq", w != null ? (long)w.FirstSeq : 0L },
                { "nextSeq", w != null ? (long)w.NextSeq : 0L }, { "sentUpTo", w != null ? (long)w.SentUpTo : 0L }, { "dropped", w != null ? (long)w.Dropped : 0L },
                { "resyncEpoch", w != null ? (long)w.ResyncEpoch : 0L }, { "resyncPending", w != null && w.ResyncPending }, { "notices", _notices }, { "writes", _writes },
                { "enqueueFailed", _enqueueFailed }, { "rowsLast", _lastRows }, { "snVersion", _c7Version }, { "snFlags", c.flags }, { "ackSeq", (long)c.ackSeq },
                { "appliedSeq", (long)c.appliedSeq }, { "snStream", (long)c.stream }, { "snResyncEpoch", (long)c.resyncEpoch }, { "reloadsPending", c.reloadsPending },
                { "batchesServed", (long)c.batchesServed }, { "reloadFailures", (long)c.reloadFailures }, { "lastLatencyMs", c.lastLatencyMs },
                { "snStoreKey", S8aStore.Hex16(c.storeKey) },
            };
        }
    }
}
