using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using DeepEngineers.SE.Terrain;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.SessionComponents.Clipboard;
using Sandbox.Game.World;
using Sandbox.Graphics;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.Utils;
using VRage.Utils;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S10 part 1 (docs/S10-BRIEF.md, the measurement rig), SE side. Two things, nothing in a hot path changes:
 /// <list type="bullet">
 /// <item><c>s10.se</c>, once a second in every flavour and phase (Plugin.UpdateCore, S10 hook): SE's own frame time
 /// <c>MyFpsManager.FrameTime</c>
 /// sampled once per plugin update (the update runs at the simulation rate, 60 Hz,
 /// so at 120 fps it sees every second drawn frame: p50/p95 of the samples, the engine's own window max
 /// <c>FrameTimeMax</c> beside them), the update interval (Stopwatch between two plugin updates: a simulation stall
 /// or a hitch shows here even when SE draws on), <c>MyFpsManager.GetFps</c>, and the paste job's state. The
 /// memory stays in the <c>sample</c> row (S8Memory).</item>
 /// <item><c>de.s10 {paste: {blocks, dist, path}}</c> (dev builds; release refuses unknown verbs, UserCommandRules):
 /// the perf scene's 10,000-block ship (<see cref="S10Blueprint"/>, vanilla blocks only) through SE's own blueprint
 /// path, as the blueprint screen does it
 /// <c>MyGridClipboard.SetGridFromBuilders(MyObjectBuilder_CubeGrid[],
 /// Vector3, float, bool)</c>, <c>MyClipboardComponent.Static</c>,
 /// <c>.Clipboard</c>, <c>.Paste</c> (the first call activates the preview: MyGridClipboard.PasteGridInternal
 /// calls Activate while not <c>IsActive</c>, a parallel task that adds <c>PreviewGrids</c>; the
 /// second call places). Selection lag = first Paste → <c>IsActive</c> with preview grids; placement wait = second
 /// Paste → the real grid of our <c>DisplayName</c> added
 /// When the
 /// clipboard refuses (no copy-paste in the world, CanBePlaced false after 5 tries), the job falls back to
 /// <c>MyEntities.CreateFromObjectBuilderParallel</c>
 /// and says so (<c>path: "direct"</c>). The grid is a test entity (<c>MyEntity.Save = false</c>, never
 /// written by an autosave); <c>{clear: true}</c> closes it (<c>MyEntity.Close</c>). Rows <c>s10.paste</c>
 /// (phase start / selected / placed / done / failed) with the times; Subnautica's spawn time is the analyzer's (the
 /// s5.budget rows' pendingParts back to 0).</item>
 /// </list>
 /// Update thread only; never throws into SE (a failure disables only s10.se, logged once).
 /// </summary>
    internal static class S10Se
    {
        public const string Verb = "de.s10";
        public const int MaxPasteTries = 5;
        public const ulong StepTimeoutMs = 180000, PreviewSettleMs = 1000, RetryMs = 500;

        private static readonly S10FrameWindow Engine = new S10FrameWindow(), Interval = new S10FrameWindow();
        private static readonly S10StageClock Stages = new S10StageClock(S10PerfRules.SeStageCount);
        private static readonly double[] StageSum = new double[S10PerfRules.SeStageCount], StageMax = new double[S10PerfRules.SeStageCount];
        private static readonly int[] StageCalls = new int[S10PerfRules.SeStageCount];
        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
        private static long _lapT;
        private static long _lastTs;
        private static ulong _windowStartMs, _rows;
        private static bool _failed;
        private static PasteJob _job, _last;

        // ------------------------------------------------------------------------------------------ the s10.se row

        public static void Tick(ulong now, bool inWorld)
        {
            if (_failed) return;
            try
            {
                long ts = Stopwatch.GetTimestamp();
                Stages.EndFrame(); // the previous update's stages
                S8aClock.EndFrame(); // round 3 F
                if (_commitUpdate > _commitMax) _commitMax = _commitUpdate; // round 3 G: the worst update's commit time
                _commitUpdate = 0;
                _clipboardTick = -1;
                if (_lastTs != 0) Interval.Add((ts - _lastTs) * 1000.0 / Stopwatch.Frequency);
                _lastTs = ts;
                Engine.Add(GameState.FrameTimeMs);
                if (_job != null) _job.Tick(now, inWorld);
                CurtainTick(now); // round 2 B: the curtain decision (S10Curtain), the s10.curtain rows
                if (now >= _nextMemMs) { _nextMemMs = now + 10000; MemRow(now, inWorld); } // round 2 A2: s10.mem every 10 s
                if (_windowStartMs == 0) { _windowStartMs = now; return; }
                if (now - _windowStartMs < 1000) return;
                double spanS = (now - _windowStartMs) / 1000.0;
                _windowStartMs = now;
                S10FrameStats e = Engine.Take(), u = Interval.Take();
                Stages.Take(MsPerTick, StageSum, StageMax, StageCalls);
                Alloc.Take(AllocKb);
                S8aClock.Take(MsPerTick, S8aSum, S8aMax, S8aCalls); // round 3 F
                var s8aSteps = new Dictionary<string, object>(StringComparer.Ordinal);
                for (int k = 0; k < S10TerrainSlice.SeSteps.Length; k++)
                    if (S8aCalls[k] > 0) s8aSteps[S10TerrainSlice.SeSteps[k]] = new Dictionary<string, object>(StringComparer.Ordinal) { { "ms", Math.Round(S8aSum[k], 3) }, { "maxUpdateMs", Math.Round(S8aMax[k], 3) } };
                var stages = new Dictionary<string, object>(StringComparer.Ordinal);
                int slot = 0;
                foreach (string stageName in S10PerfRules.SeStageNames)
                {
                    var st = new Dictionary<string, object>(StringComparer.Ordinal)
                    { { "ms", Math.Round(StageSum[slot], 3) }, { "maxUpdateMs", Math.Round(StageMax[slot], 3) }, { "calls", StageCalls[slot] } };
                    if (AllocStages) st["allocKB"] = Math.Round(AllocKb[slot], 1); // round 3 E: bytes the update thread allocated in the stage (exact)
                    stages[stageName] = st;
                    slot++;
                }
                double commitMs = _commitSec * MsPerTick, commitMaxMs = _commitMax * MsPerTick; // round 3 G
                var export = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "readMs", Math.Round(Math.Max(0, StageSum[S10PerfRules.SeStageExport] - commitMs), 3) }, { "commitMs", Math.Round(commitMs, 3) },
                    { "commitMaxUpdateMs", Math.Round(commitMaxMs, 3) }, { "exportMaxUpdateMs", Math.Round(StageMax[S10PerfRules.SeStageExport], 3) },
                    { "parts", _commitParts }, { "thread", false },
                };
                _commitSec = _commitMax = _commitParts = 0;
                long threadBytes = AllocStages ? GC.GetAllocatedBytesForCurrentThread() : 0;
                double threadMBps = AllocStages && _lastThreadBytes > 0 && spanS > 0 ? (threadBytes - _lastThreadBytes) / 1048576.0 / spanS : double.NaN;
                _lastThreadBytes = threadBytes;
                if (_job != null) _job.WindowMaxMs = Math.Max(_job.WindowMaxMs, double.IsNaN(e.Max) ? 0 : e.Max);
                Jsonl.WriteLineBuffered(S4Rows.Row(S10PerfRules.RowSe, "seq", ++_rows, "spanS", spanS, "inWorld", inWorld,
                    "updates", u.Frames + 1, "engineSamples", e.Frames, "engineFrameP50Ms", e.P50, "engineFrameP95Ms", e.P95,
                    "engineFrameMaxMs", e.Max, "engineWindowMaxMs", (double)GameState.FrameTimeMaxMs, "fps", GameState.Fps,
                    "updateP50Ms", u.P50, "updateP95Ms", u.P95, "updateMaxMs", u.Max, "paste", _job != null ? _job.State : (_last != null ? _last.State : "none"),
                    "stages", stages, "previewCap", PreviewCap, "previewRefused", PreviewRefused, "lightsPerGrid", LightsPerGrid, "lightsCapped", LightsCapped,
                    "colliderNearM", ColliderNearM, "skinsDeferredFar", SkinsDeferredFar, "previewImpostors", PreviewImpostors, "preset", Preset.Name,
                    "allocCuts", S10AllocCuts.On, "updateAllocMBps", threadMBps, "gc0", GC.CollectionCount(0), "gc1", GC.CollectionCount(1), "gc2", GC.CollectionCount(2),
                    "rowsSkipped", RowsSkipped, "s8aSteps", s8aSteps, "export", export, "outcropsBoxes", S8aOutcropsSe.S10BoxesOn));
            }
            catch (Exception ex)
            {
                _failed = true;
                DeLog.Info("DE.S10 s10.se off after an exception (measurement only): " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ------------------------------------------------------------------------------------------ milestone B: the curtain

 /// <summary>The curtain (env DEEPENGINEERS_S10_CURTAIN off, or de.s10 {curtain: off}: SE's picture as before).</summary>
        public static bool CurtainOn = EnvOn("DEEPENGINEERS_S10_CURTAIN", true);
        /// <summary>

 /// killing the character or the ship"): while the curtain is wanted SE's simulation is paused (MySandboxGame.PausePush, the
 /// pause the Escape menu uses; offline single player only, MySession.IsPausable) and popped the moment Subnautica shows. The
 /// curtain decision keeps running: it is computed in the plugin's per-frame Update, not in the session's update. A pause is
 /// never held longer than PauseMaxS (a stuck renderer would otherwise freeze the game until the 60 s give-up).
 /// Env DEEPENGINEERS_S10_CURTAINPAUSE off, or de.s10 {curtainPause: off}: the curtain without the pause.
 /// </summary>
 /// <summary>

 /// (S10.CURTAIN.SHOWMS streaming p50 20,332 ms): while SE's session is paused the frames and the camera stop reaching
 /// Subnautica, so it never reports the area built, and the curtain only lifts when the cap releases the pause. The pause
 /// needs the bridge to keep ticking under it (a later change); on = DEEPENGINEERS_S10_CURTAINPAUSE=on for tests.
 /// </summary>
        public static bool CurtainPauseOn = EnvOn("DEEPENGINEERS_S10_CURTAINPAUSE", false);
        public const double PauseMaxS = 20.0;
        private static bool _pausedByUs;
        private static long _pausedTs;
        public static long PausePushes, PausePops;
        private static string _pauseDisabled;

        private static void PauseHold(bool hold, long ts)
        {
            if (_pauseDisabled != null) hold = false;
            if (hold && _pausedByUs && (ts - _pausedTs) * 1.0 / Stopwatch.Frequency > PauseMaxS) { hold = false; DeLog.Info("DE.S10 curtain pause released after " + PauseMaxS + " s (" + _cause + "): the game runs behind the curtain from here"); }
            if (hold == _pausedByUs) return;
            try
            {
                if (hold)
                {
                    MySession session = MySession.Static;
                    if (session == null || !session.IsPausable()) return;
                    MySandboxGame.PausePush();
                    _pausedByUs = true;
                    _pausedTs = ts;
                    PausePushes++;
                    if (PausePushes <= 5) DeLog.Info("DE.S10 curtain: SE paused behind the curtain (" + _cause + ")");
                }
                else
                {
                    MySandboxGame.PausePop();
                    _pausedByUs = false;
                    PausePops++;
                    if (PausePops <= 5) DeLog.Info("DE.S10 curtain: SE resumed (" + (_snShown ? "Subnautica shows" : _cause) + ", paused " + Math.Round((ts - _pausedTs) * 1000.0 / Stopwatch.Frequency) + " ms)");
                }
            }
            catch (Exception e)
            {
                _pauseDisabled = e.GetType().Name + ": " + e.Message;
                _pausedByUs = false;
                DeLog.Info("DE.S10 curtain pause off after an exception: " + _pauseDisabled);
            }
        }
        public static double CurtainFadeMs = S10Curtain.DefaultFadeMs;
        private static bool _curtainWanted, _inZone, _snShown, _awaitingSettle, _haveCam, _episode;
        private static string _cause = "none", _episodeCause = "none";
        private static long _shownTs, _episodeStartTs, _exitedSinceTs;
        private static Vector3D _lastCam;
        private static ulong _nextCurtainRowMs;
        // frames presented (SessionComponent.Draw, the GUI pass of every drawn frame) in the zone, under the curtain, with
 // Subnautica's picture, and exposed: SE's own picture in the zone without Subnautica's and without the curtain
        private static long _fZone, _fCurtain, _fSn, _fExposed, _episodes, _totalExposed;
        private static double _episodeMaxMs;
        private static string _curtainDisabled;

        /// <summary>
 /// Update thread, once per update (before S2Host steps the view machine: the decision reads the previous tick's
 /// outputs, one update late). A camera jump of more than 50 m in one update (MySector.MainCamera, as the producer
 /// reads it) or a view event (teleport, world load, new renderer generation, link recovery) arms the settle wait;
 /// it clears when Subnautica reports its area built and streaming settled again. Never throws.
 /// </summary>
        private static void CurtainTick(ulong now)
        {
            try
            {
                ViewStateDriver d = S2Host.Driver;
                bool active = CurtainOn && _curtainDisabled == null && d != null && d.Active;
                ViewOutputs o = d != null ? d.Last : default(ViewOutputs);
                ViewInputs vi = d != null ? d.LastInputs : default(ViewInputs);
                MyCamera cam = MySector.MainCamera;
                if (cam != null)
                {
                    Vector3D p = cam.WorldMatrix.Translation;
                    double jump = _haveCam ? Vector3D.Distance(p, _lastCam) : 0;
                    if (_haveCam && jump > S10Curtain.DefaultJumpM) { _awaitingSettle = true; _lastJumpM = jump; } // round 4: the jump that armed the wait
                    _lastCam = p;
                    _haveCam = true;
                }
                if ((vi.Events & S10Curtain.EventsNeedingSettle) != 0) _awaitingSettle = true;
                _inZone = active && S10Curtain.InZone(d.Active, o.PicturePolicy, d.Y, ViewThresholds.ApproachEnterY, o.ViewState);
                if (_awaitingSettle && vi.Peer == PeerState.Alive && vi.SnPhase == SnPhase.Active && (o.Blocked & S10Curtain.SettleBlocks) == 0 && o.Picture) _awaitingSettle = false;
                bool shown = S10Curtain.SnShown(o.Picture, o.MarginsOn, o.SeLowered, vi.Peer, vi.SnPhase, o.Blocked, _awaitingSettle);
                _cause = S10Curtain.Cause(o.Picture, o.MarginsOn, o.SeLowered, vi.Peer, vi.SnPhase, _awaitingSettle);
                long ts = Stopwatch.GetTimestamp();
                // the renderer gone for a minute: the curtain gives way (the player is never left in the dark; the row says so)
                if (vi.Peer == PeerState.Exited) { if (_exitedSinceTs == 0) _exitedSinceTs = ts; }
                else _exitedSinceTs = 0;
                bool givenUp = _exitedSinceTs != 0 && (ts - _exitedSinceTs) * 1.0 / Stopwatch.Frequency > S10Curtain.DefaultGiveUpS;
                bool wanted = _inZone && !shown && !givenUp;
                if (_snShown != shown && shown) _shownTs = ts;
                _snShown = shown;
                if (wanted && !_episode) { _episode = true; _episodeStartTs = ts; _episodeCause = _cause; _epTicks = _epArea = _epSettle = _epNoPic = _epSnPhase = 0; }
                if (wanted)
                {
                    // round 4 (the curtain after a death): which condition held the curtain, tick by tick, for the episode row
                    _epTicks++;
                    if ((o.Blocked & ViewBlock.AreaBuilt) != 0) _epArea++;
                    if ((o.Blocked & ViewBlock.Settle) != 0) _epSettle++;
                    if (!o.Picture) _epNoPic++;
                    if (vi.SnPhase != SnPhase.Active) _epSnPhase++;
                }
                if (!wanted && _episode)
                {
                    _episode = false;
                    double ms = (ts - _episodeStartTs) * 1000.0 / Stopwatch.Frequency;
                    _episodes++;
                    if (ms > _episodeMaxMs) _episodeMaxMs = ms;
                    Jsonl.WriteLineBuffered(S4Rows.Row("s10.curtain", "phase", "episode", "cause", _episodeCause, "showMs", Math.Round(ms, 1),
                        "endedBy", givenUp ? "gaveUp" : shown ? "snShown" : "leftZone", "jumpM", Math.Round(_lastJumpM, 1), "ticks", _epTicks,
                        "ticksAreaBuilt", _epArea, "ticksSettle", _epSettle, "ticksNoPicture", _epNoPic, "ticksSnNotActive", _epSnPhase, "hudHidden", _hudHiddenByUs));
                    _lastJumpM = 0;
                }
                _curtainWanted = wanted;
                PauseHold(CurtainPauseOn && wanted, ts); //: nothing happens behind the curtain
                HudHold(CurtainHudOn && wanted);         // round 4: SE's HUD hidden under the curtain (MyHud.IsVisible), restored on lift
                GcTrimTick(wanted, ts);                  // round 4: one compacting collection per world load, under the curtain
                if (now >= _nextCurtainRowMs)
                {
                    _nextCurtainRowMs = now + 1000;
                    Jsonl.WriteLineBuffered(S4Rows.Row("s10.curtain", "phase", "second", "on", CurtainOn, "inZone", _inZone, "wanted", wanted, "cause", _cause,
                        "framesZone", _fZone, "framesCurtain", _fCurtain, "framesSn", _fSn, "framesExposed", _fExposed, "exposedTotal", _totalExposed,
                        "episodes", _episodes, "episodeMaxMs", Math.Round(_episodeMaxMs, 1), "awaitingSettle", _awaitingSettle, "disabled", _curtainDisabled ?? "",
                        "pause", CurtainPauseOn, "paused", _pausedByUs, "pausePushes", PausePushes, "pauseDisabled", _pauseDisabled ?? ""));
                    _fZone = _fCurtain = _fSn = _fExposed = 0;
                }
            }
            catch (Exception e)
            {
                _curtainDisabled = e.GetType().Name + ": " + e.Message;
                _curtainWanted = false;
                DeLog.Info("DE.S10 curtain off after an exception: " + _curtainDisabled);
                PauseHold(false, Stopwatch.GetTimestamp());
                HudHold(false);
            }
        }

        // ------------------------------------------------------------------------------------------ round 4: HUD and the GC trim

 /// <summary>Round 4: SE's HUD screen draws after SessionComponent.Draw every frame
 /// so it showed
 /// over the curtain. While the curtain is wanted the HUD is hidden; on lift it is restored only if we hid it. Env
 /// DEEPENGINEERS_S10_CURTAINHUD off: the HUD as before.</summary>
        public static bool CurtainHudOn = EnvOn("DEEPENGINEERS_S10_CURTAINHUD", true);
        private static double _lastJumpM;
        private static long _epTicks, _epArea, _epSettle, _epNoPic, _epSnPhase;
        private static bool _hudHiddenByUs;
        public static long HudHides;

        /// MyHud.UpdateBeforeSimulation sets MyHud.IsVisible again on every update,
 /// so hiding it through that flag only lasted until SE's next update. The HUD screen's PrepareDraw and Draw are skipped
 /// instead while this is true (<see cref="PatchHud"/>); MyHud.IsVisible is never written.</summary>
        public static volatile bool HudSuppressed;

        private static void HudHold(bool hide)
        {
            if (hide && !_hudHiddenByUs) HudHides++;
            _hudHiddenByUs = hide;
            HudSuppressed = hide && HudPatchOk;
        }

        public static bool HudPatchOk;

        /// <summary>Prefixes on MyGuiScreenHudSpace.PrepareDraw (the controlled entity's HUD: ore markers, GPS) and Draw (the
 /// toolbar, the stats, every HUD control): false skips them while the curtain is up.</summary>
        public static void PatchHud(HarmonyLib.Harmony harmony)
        {
            try
            {
                Type t = typeof(Sandbox.Game.Gui.MyHud).Assembly.GetType("Sandbox.Game.Gui.MyGuiScreenHudSpace");
                System.Reflection.MethodInfo prep = t != null ? t.GetMethod("PrepareDraw", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public, null, Type.EmptyTypes, null) : null;
                System.Reflection.MethodInfo draw = t != null ? t.GetMethod("Draw", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public, null, Type.EmptyTypes, null) : null;
                if (harmony == null || prep == null || draw == null || draw.ReturnType != typeof(bool))
                {
                    DeLog.Info("DE.Harmony MISSING S10 MyGuiScreenHudSpace.PrepareDraw/Draw (" + (t == null ? "type not found" : "method not found") + "): the HUD stays visible under the curtain");
                    return;
                }
                harmony.Patch(prep, prefix: new HarmonyLib.HarmonyMethod(typeof(S10Se).GetMethod(nameof(HudPrepareDrawPrefix), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)));
                harmony.Patch(draw, prefix: new HarmonyLib.HarmonyMethod(typeof(S10Se).GetMethod(nameof(HudDrawPrefix), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)));
                HudPatchOk = true;
                DeLog.Info("DE.Harmony S10 OK MyGuiScreenHudSpace.PrepareDraw() and Draw() prefixes (the HUD skipped under the curtain)");
            }
            catch (Exception e)
            {
                HudPatchOk = false;
                DeLog.Info("DE.Harmony S10 HUD patch failed: " + e.GetType().Name + ": " + e.Message + " (the HUD stays visible under the curtain)");
            }
        }

        public static bool HudPrepareDrawPrefix() { return !HudSuppressed; }

        public static bool HudDrawPrefix(ref bool __result)
        {
            if (!HudSuppressed) return true;
            __result = false;
            return false;
        }

        /// <summary>
 /// Round 4 (SE's 17.1 GB while the clipmaps were built
 /// then held 1.5 GB, but private bytes stayed at 17.6 GB with a 10 GB working set: the collector keeps the committed
 /// segments of that peak): once per world load, after the curtain has been up for 2 s (SE's picture hidden, Subnautica
 /// still loading), one blocking compacting collection with the large-object heap compacted
 /// (GCSettings.LargeObjectHeapCompactionMode = CompactOnce; GC.Collect(2, Forced, blocking, compacting)) so the free
 /// segments go back to Windows. Its pause falls under the curtain. Row s10.mem phase "trim" (private bytes before/after,
 /// the pause). Env DEEPENGINEERS_S10_GCTRIM off: none.
 /// </summary>
        public static bool GcTrimOn = EnvOn("DEEPENGINEERS_S10_GCTRIM", true);
        private static uint _gcTrimEpoch = uint.MaxValue;
        private static long _wantedSinceTs;

        private static void GcTrimTick(bool wanted, long ts)
        {
            if (!wanted) { _wantedSinceTs = 0; return; }
            if (_wantedSinceTs == 0) _wantedSinceTs = ts;
            if (!GcTrimOn || _gcTrimEpoch == Phases.WorldEpoch || ts - _wantedSinceTs < 2 * Stopwatch.Frequency) return;
            _gcTrimEpoch = Phases.WorldEpoch;
            try
            {
                double before = PrivateMB(), heapBefore = GC.GetTotalMemory(false) / 1048576.0;
                long t0 = Stopwatch.GetTimestamp();
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                double after = PrivateMB(), heapAfter = GC.GetTotalMemory(false) / 1048576.0;
                Jsonl.WriteLineBuffered(S4Rows.Row("s10.mem", "phase", "trim", "worldEpoch", (long)Phases.WorldEpoch, "privateBeforeMB", before, "privateAfterMB", after,
                    "gcHeapBeforeMB", heapBefore, "gcHeapAfterMB", heapAfter, "pauseMs", Math.Round(ms, 1), "cause", _cause, "serverGc", System.Runtime.GCSettings.IsServerGC));
                DeLog.Info("DE.S10 GC trim under the curtain: private " + before.ToString("0", CultureInfo.InvariantCulture) + " -> " + after.ToString("0", CultureInfo.InvariantCulture) +
                           " MB, managed " + heapBefore.ToString("0", CultureInfo.InvariantCulture) + " -> " + heapAfter.ToString("0", CultureInfo.InvariantCulture) + " MB in " + ms.ToString("0", CultureInfo.InvariantCulture) + " ms");
            }
            catch (Exception e) { DeLog.Info("DE.S10 GC trim failed: " + e.GetType().Name + ": " + e.Message); }
        }

        private static double PrivateMB()
        {
            MemCounters c;
            return K32GetProcessMemoryInfo(new IntPtr(-1), out c, (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MemCounters))) ? c.PrivateUsage.ToUInt64() / 1048576.0 : double.NaN;
        }

        private const string CurtainTexture = "Textures\\GUI\\Blank.dds";

        /// <summary>
 /// SessionComponent.Draw (the GUI pass, never skipped by design F; S10 hook): the curtain over SE's picture while it is
 /// wanted or fading, with one line saying why; and the frame counts of the s10.curtain row. A full-screen sprite as the
 /// crossing flash draws it (<c>MyGuiManager.DrawSpriteBatch(string, Vector2, Vector2, Color, MyGuiDrawAlignEnum, bool)</c>,
 /// CrossingFlash.cs) and the line with <c>MyGuiManager.DrawString(string font, string text, Vector2, float scale)</c>
 /// A failure disables only the curtain (logged once). Never throws.
 /// </summary>
        public static void DrawCurtain()
        {
            if (_inZone) _fZone++;
            if (_snShown && _inZone) _fSn++;
            if (_curtainDisabled != null) { if (_inZone && !_snShown) { _fExposed++; _totalExposed++; } return; }
            double a = S10Curtain.Alpha(_curtainWanted, _snShown ? (Stopwatch.GetTimestamp() - _shownTs) * 1000.0 / Stopwatch.Frequency : double.NaN, CurtainFadeMs);
            if (!_inZone) a = 0;
            if (a <= 0)
            {
                if (_inZone && !_snShown) { _fExposed++; _totalExposed++; }
                return;
            }
            try
            {
                MyGuiManager.DrawSpriteBatch(CurtainTexture, Vector2.Zero, Vector2.One, new Color(0, 0, 0, (byte)Math.Round(a * 255.0)), MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP, true);
                if (_curtainWanted)
                {
                    string line = _cause == "exited" ? "Subnautica stopped: waiting for the renderer" : _cause == "stalled" ? "Subnautica is not responding..."
                                : _cause == "streaming" ? "Subnautica is loading the area..." : "Subnautica is getting ready...";
                    MyGuiManager.DrawString("White", line, new Vector2(0.40f, 0.52f), 0.9f);
                }
                if (a >= 0.999) _fCurtain++;
                else if (_inZone && !_snShown) { _fExposed++; _totalExposed++; }
            }
            catch (Exception e)
            {
                _curtainDisabled = e.GetType().Name + ": " + e.Message;
                DeLog.Info("DE.S10 curtain draw disabled: " + _curtainDisabled);
            }
        }

        // ------------------------------------------------------------------------------------------ milestone A2: memory

        private static ulong _nextMemMs, _lastFaultsMs;
        private static long _lastFaults = -1;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MemCounters
        {
            public uint cb, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool K32GetProcessMemoryInfo(IntPtr hProcess, out MemCounters memoryCounters, uint sizeBytes);

        /// <summary>
 /// s10.mem every 10 s (round 2 A2, the S10.MEM.* rows): SE's private bytes, working set and page faults per second
 /// (K32GetProcessMemoryInfo on the current-process pseudo handle), the managed heap, and our shares: the mirror's
 /// model payloads kept for re-streams, the C3/C4 queues, the mirrored grids, the S4 tiles' private-bytes step at
 /// creation. Update thread; never throws.
 /// </summary>
        private static void MemRow(ulong now, bool inWorld)
        {
            try
            {
                MemCounters c;
                long priv = -1, ws = -1, faults = -1;
                if (K32GetProcessMemoryInfo(new IntPtr(-1), out c, (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MemCounters))))
                {
                    priv = (long)c.PrivateUsage.ToUInt64();
                    ws = (long)c.WorkingSetSize.ToUInt64();
                    faults = c.PageFaultCount;
                }
                double rate = _lastFaults >= 0 && now > _lastFaultsMs && faults >= 0 ? (faults - _lastFaults) * 1000.0 / (now - _lastFaultsMs) : double.NaN;
                _lastFaults = faults;
                _lastFaultsMs = now;
                long payload, models, c3, grids;
                GridMirrorProducer.S10Memory(out payload, out models, out c3, out grids);
                // round 3 (the 00:02 session: 15.2 GB with 1.5 GB accounted): our terrain items. The octree source and the prefetch
 // map Subnautica's batch files read-only (file-backed pages: working set, not private bytes); the outcrops scan's
 // dirty set grew per cell of every changed range before the round 3 boxes
                long dirty = -1, boxes = -1, consumed = -1, ocCells = -1, ocMem = -1, mapped = -1, batchesMapped = -1, pfData = -1, pfPages = -1, drillPending = -1, overlayCells = -1;
                try { S8aOutcropsSe.S10Sizes(out dirty, out boxes, out consumed, out ocCells, out ocMem); } catch (Exception) { }
                try { var src = Terrain.TerrainTiles.Source; var ss = src != null ? src.GetStats() : null; if (ss != null) { mapped = ss.BytesMapped; batchesMapped = ss.BatchesMapped; } } catch (Exception) { }
                try { var pf = Terrain.TerrainTiles.Prefetch; if (pf != null) { pfData = pf.Data; pfPages = pf.PagesTouched; } } catch (Exception) { }
                try { drillPending = Terrain.S8aDrillConverter.Pending; overlayCells = Terrain.S8aOverlay.Cells; } catch (Exception) { }
                Jsonl.WriteLineBuffered(S4Rows.Row("s10.mem", "inWorld", inWorld, "privateMB", priv >= 0 ? priv / 1048576.0 : double.NaN,
                    "workingSetMB", ws >= 0 ? ws / 1048576.0 : double.NaN, "pageFaultsPerSec", rate, "gcHeapMB", GC.GetTotalMemory(false) / 1048576.0,
                    "mirrorModels", models, "modelPayloadMB", payload / 1048576.0, "c3QueueMB", c3 / 1048576.0, "mirroredGrids", grids,
                    "tilesStepMB", Terrain.TerrainTiles.RamAfterBytes > 0 && Terrain.TerrainTiles.RamBeforeBytes > 0
                        ? (Terrain.TerrainTiles.RamAfterBytes - Terrain.TerrainTiles.RamBeforeBytes) / 1048576.0 : double.NaN,
                    "outcropsDirty", dirty, "outcropsBoxes", boxes, "outcropsConsumed", consumed, "outcropsCells", ocCells, "outcropsMemory", ocMem,
                    "sourceMappedMB", mapped >= 0 ? mapped / 1048576.0 : double.NaN, "batchesMapped", batchesMapped, "prefetchData", pfData, "prefetchPages", pfPages,
                    "drillPending", drillPending, "overlayCells", overlayCells, "gcGen2", GC.CollectionCount(2)));
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------------------------------ stage clock (part 3)

 /// <summary>A Stopwatch timestamp (the start of a measured step); round 3 E: the update thread's allocated bytes beside it.</summary>
        public static long T()
        {
            long ts = Stopwatch.GetTimestamp();
            if (AllocStages) Alloc.Mark(ts, GC.GetAllocatedBytesForCurrentThread());
            return ts;
        }

        /// <summary>The step that started at <paramref name="t0"/> ends now (<see cref="S10PerfRules"/> SeStage slot).</summary>
        public static void Add(int stage, long t0)
        {
            Stages.Add(stage, Stopwatch.GetTimestamp() - t0);
            if (AllocStages) Alloc.Add(stage, t0, GC.GetAllocatedBytesForCurrentThread());
        }

        /// <summary>Starts a lap sequence (Plugin.UpdateCore, before the mirror producer).</summary>
        public static void LapStart()
        {
            _lapT = Stopwatch.GetTimestamp();
            if (AllocStages) Alloc.LapStart(GC.GetAllocatedBytesForCurrentThread());
        }

        /// <summary>The time since the previous lap mark goes to <paramref name="stage"/>.</summary>
        public static void Lap(int stage)
        {
            long t = Stopwatch.GetTimestamp();
            if (_lapT != 0) Stages.Add(stage, t - _lapT);
            _lapT = t;
            if (AllocStages) Alloc.Lap(stage, GC.GetAllocatedBytesForCurrentThread());
        }

        // ------------------------------------------------------------------------------------------ round 3 E: allocations

 /// <summary>Round 3 E: bytes allocated per stage on the update thread (GC.GetAllocatedBytesForCurrentThread,.NET 4.8:
 /// exact for this thread; s10.se <c>stages.*.allocKB</c> and <c>updateAllocMBps</c>; env DEEPENGINEERS_S10_ALLOCSTAGES off).</summary>
        public static bool AllocStages = EnvOn("DEEPENGINEERS_S10_ALLOCSTAGES", true);
        private static readonly S10AllocMeter Alloc = new S10AllocMeter(S10PerfRules.SeStageCount);

        // ------------------------------------------------------------------------------------------ round 3 F: SE's S8a host

 /// <summary>Round 3 F: S8aHost.Tick's steps (<see cref="S10TerrainSlice.SeSteps"/>) on a lap clock (s10.se <c>s8aSteps</c>:
 /// ms per second and the worst update per step): which step owns the s8a stage's worst updates. Update thread only.</summary>
        private static readonly S10StageClock S8aClock = new S10StageClock(S10TerrainSlice.SeSteps.Length);
        private static readonly double[] S8aSum = new double[S10TerrainSlice.SeSteps.Length], S8aMax = new double[S10TerrainSlice.SeSteps.Length];
        private static readonly int[] S8aCalls = new int[S10TerrainSlice.SeSteps.Length];
        private static long _s8aLapT;

        // ------------------------------------------------------------------------------------------ round 3 G: the export split

 /// <summary>Round 3 G: the export stage split on the update thread: GridExporter.Commit (the diff, the PartAdd/Remove/
 /// Recolor serialization, the C4 enqueue, the s5.edit rows) timed apart from the reads of SE's render cells and fat blocks
 /// (the snapshot, game-thread state); s10.se <c>export</c> {readMs, commitMs, commitMaxUpdateMs, readMaxUpdateMs, parts}.</summary>
        private static long _commitSec, _commitUpdate, _commitMax, _commitParts;

        public static void ExportCommit(long t0, int parts)
        {
            long d = Stopwatch.GetTimestamp() - t0;
            if (d < 0) return;
            _commitSec += d;
            _commitUpdate += d;
            _commitParts += parts;
        }

        /// <summary>S8aHost.Tick starts (after its switch check).</summary>
        public static void S8aStart() { _s8aLapT = Stopwatch.GetTimestamp(); }

        /// <summary>The time since the previous S8a lap mark goes to step <paramref name="step"/>.</summary>
        public static void S8aLap(int step)
        {
            long t = Stopwatch.GetTimestamp();
            if (_s8aLapT != 0) S8aClock.Add(step, t - _s8aLapT);
            _s8aLapT = t;
        }
        private static readonly double[] AllocKb = new double[S10PerfRules.SeStageCount];
        private static long _lastThreadBytes;

        /// <summary>Round 3 E: per-second budgets of high-volume SE rows (the s12.flood level rows; state changes always pass),
 /// under <see cref="S10AllocCuts"/>. A row not written allocates nothing; counted in s10.se rowsSkipped.</summary>
        public const int RowFlood = 0, RowSlots = 1;
        private static readonly int[] _rowsUsed = new int[RowSlots];
        private static long _rowsSecondTs;
        public static long RowsSkipped;

        public static bool TakeRow(int slot)
        {
            if (!S10AllocCuts.On || slot < 0 || slot >= RowSlots) return true;
            long ts = Stopwatch.GetTimestamp();
            if (ts - _rowsSecondTs >= Stopwatch.Frequency) { _rowsSecondTs = ts; Array.Clear(_rowsUsed, 0, _rowsUsed.Length); }
            if (_rowsUsed[slot] < S10AllocCuts.RowsPerSecond) { _rowsUsed[slot]++; return true; }
            RowsSkipped++;
            return false;
        }

        // ------------------------------------------------------------------------------------------ part 3 settings

 /// <summary>The paste preview cap (<see cref="S10PastePreview"/>; env DEEPENGINEERS_S10_PREVIEWCAP, de.s10 {previewCap}).</summary>
 /// <summary>Round 2 D: the preset (env DEEPENGINEERS_S10_PRESET low | mid | high; the launcher's auto-pick); every
 /// knob's own environment variable still wins over it.</summary>
        public static readonly S10Preset Preset = S10Presets.Of(Environment.GetEnvironmentVariable(S10Presets.Env));
        public static int PreviewCap = EnvInt(S10PastePreview.Env, Preset.PreviewCap);
        /// <summary>Lights one grid may give SE's light table (<see cref="S10LightCap"/>; env DEEPENGINEERS_S10_LIGHTSPERGRID, de.s10 {lightsPerGrid}).</summary>
        public static int LightsPerGrid = EnvInt("DEEPENGINEERS_S10_LIGHTSPERGRID", Preset.LightsPerGrid);
        /// <summary>S6 collision skins are built only for grids within this camera distance (env DEEPENGINEERS_S10_COLLIDERNEARM,
 /// de.s10 {colliderNearM}; 0 or less: every mirrored grid, the S6 rule). A skin already built stays when its grid moves away.</summary>
        public static double ColliderNearM = EnvDouble("DEEPENGINEERS_S10_COLLIDERNEARM", Preset.ColliderNearM);
        public static long PreviewRefused, PreviewAdmitted, LightsCapped, SkinsDeferredFar, PreviewImpostors;
        /// <summary>Round 2 milestone A: a paste preview above <see cref="PreviewCap"/> is mirrored as an impostor instead of

 /// bit (Subnautica builds large parts only, one flat colour, no shadows, no children) and the export holds back its
 /// edges and subparts (S7t's filter). Env DEEPENGINEERS_S10_PREVIEWIMPOSTOR off: above the cap nothing, as part 3.</summary>
        public static bool PreviewImpostorOn = EnvOn("DEEPENGINEERS_S10_PREVIEWIMPOSTOR", true);
        private static bool _clipboardOverCap;

        private static bool EnvOn(string name, bool dflt)
        {
            string s = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(s)) return dflt;
            s = s.Trim().ToLowerInvariant();
            return s == "on" || s == "1" || s == "true" || (s != "off" && s != "0" && s != "false" && dflt);
        }

        /// <summary>The grid is a clipboard preview drawn as an impostor (over the cap, impostor mode on); the producer's
 /// GridAdd flags and S7t's export filter ask it. Never throws.</summary>
        public static bool PreviewImpostor(MyCubeGrid g)
        {
            try { return PreviewImpostorOn && g != null && g.IsPreview && _clipboardOverCap && g.Projector == null; }
            catch (Exception) { return false; }
        }
        private static int _clipboardBlocks;
        private static long _clipboardTick = -1;
        private static readonly List<GridMirrorProducer.MirroredGrid> NearList = new List<GridMirrorProducer.MirroredGrid>(64);

        private static int EnvInt(string name, int dflt)
        {
            int v;
            string s = Environment.GetEnvironmentVariable(name);
            return !string.IsNullOrEmpty(s) && int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : dflt;
        }

        private static double EnvDouble(string name, double dflt)
        {
            double v;
            string s = Environment.GetEnvironmentVariable(name);
            return !string.IsNullOrEmpty(s) && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : dflt;
        }

        /// <summary>
 /// S7p hook (S7pProjection.Verdict, a pasting preview): whether the clipboard's preview may be mirrored. The blocks of
 /// every preview grid of the clipboard
 /// summed once per update. Never throws (a failure admits: the S7p rules alone).
 /// </summary>
        public static bool PastePreviewAllowed()
        {
            try
            {
                if (_clipboardTick != 0)
                {
                    _clipboardTick = 0;
                    int sum = 0;
                    MyClipboardComponent c = MyClipboardComponent.Static;
                    MyGridClipboard cb = c != null ? c.Clipboard : null;
                    if (cb != null && cb.PreviewGrids != null)
                        foreach (MyCubeGrid g in cb.PreviewGrids) if (g != null) sum += g.BlocksCount;
                    _clipboardBlocks = sum;
                }
                bool ok = S10PastePreview.Admit(_clipboardBlocks, PreviewCap);
                _clipboardOverCap = !ok;
                if (ok) PreviewAdmitted++;
                else if (PreviewImpostorOn) { PreviewImpostors++; return true; } // round 2 A: admitted, drawn as an impostor (PreviewImpostor)
                else PreviewRefused++;
                return ok;
            }
            catch (Exception) { return true; }
        }

        /// <summary>The mirrored grids within <see cref="ColliderNearM"/> of the camera (GridMirrorProducer's camera distance),
 /// for the S6 collision skins (colliders near the character only). The list is reused; the input order is kept.</summary>
        public static List<GridMirrorProducer.MirroredGrid> NearOnly(List<GridMirrorProducer.MirroredGrid> grids)
        {
            if (grids == null || ColliderNearM <= 0) return grids;
            NearList.Clear();
            foreach (GridMirrorProducer.MirroredGrid g in grids)
            {
                if (g.Distance <= ColliderNearM) NearList.Add(g);
                else SkinsDeferredFar++;
            }
            return NearList;
        }

        // ------------------------------------------------------------------------------------------ de.s10

        public static Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            if (cmd != Verb) return null;
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                if (!BuildFlavor.IsDev) { d["ok"] = false; d["error"] = UserCommandRules.RefusalText; return d; }
                args = args ?? new Dictionary<string, object>();
                object v;
                if (args.TryGetValue("paste", out v)) return StartPaste(d, v as IDictionary<string, object> ?? new Dictionary<string, object>(), via);
                if (args.ContainsKey("clear")) return Clear(d);
                if (args.TryGetValue("previewCap", out v)) PreviewCap = (int)Num(args, "previewCap", PreviewCap);
                if (args.TryGetValue("lightsPerGrid", out v)) LightsPerGrid = (int)Num(args, "lightsPerGrid", LightsPerGrid);
                if (args.TryGetValue("colliderNearM", out v)) ColliderNearM = Num(args, "colliderNearM", ColliderNearM);
                if (args.TryGetValue("allocCuts", out v)) S10AllocCuts.On = S10AllocCuts.Parse(Convert.ToString(v, CultureInfo.InvariantCulture), S10AllocCuts.On); // round 3 E: before/after in one session
                if (args.TryGetValue("allocStages", out v)) AllocStages = S10AllocCuts.Parse(Convert.ToString(v, CultureInfo.InvariantCulture), AllocStages);
                if (args.TryGetValue("curtain", out v))
                {
                    string cs = Convert.ToString(v, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
                    CurtainOn = cs == "on" || cs == "true" || cs == "1";
                    _curtainDisabled = null;
                }
                d["curtain"] = CurtainOn;
                if (args.TryGetValue("curtainPause", out v)) { string cp = (v as string ?? "").Trim().ToLowerInvariant(); CurtainPauseOn = cp == "on" || cp == "true" || cp == "1"; if (!CurtainPauseOn) PauseHold(false, Stopwatch.GetTimestamp()); }
                d["curtainPause"] = CurtainPauseOn; d["paused"] = _pausedByUs;
                d["curtainCause"] = _cause;
                d["curtainExposedTotal"] = _totalExposed;
                d["previewCap"] = PreviewCap;
                d["previewRefused"] = PreviewRefused;
                d["lightsPerGrid"] = LightsPerGrid;
                d["lightsCapped"] = LightsCapped;
                d["colliderNearM"] = ColliderNearM;
                d["skinsDeferredFar"] = SkinsDeferredFar;
                d["allocCuts"] = S10AllocCuts.On;
                d["allocStages"] = AllocStages;
                d["rowsSkipped"] = RowsSkipped;
                PasteJob j = _job ?? _last;
                d["ok"] = true;
                d["paste"] = j != null ? j.Describe() : (object)"none";
                d["active"] = _job != null;
                d["rows"] = _rows;
                d["line"] = "DE.S10 status: s10.se " + (_failed ? "off" : "on") + ", paste " + (j != null ? j.State : "none");
                return d;
            }
            catch (Exception e)
            {
                d["ok"] = false;
                d["error"] = "de.s10: " + e.GetType().Name + ": " + e.Message;
                return d;
            }
        }

        private static double Num(IDictionary<string, object> a, string k, double dflt)
        {
            object v;
            if (a == null || !a.TryGetValue(k, out v) || v == null) return dflt;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch (Exception) { return dflt; }
        }

        private static Dictionary<string, object> StartPaste(Dictionary<string, object> d, IDictionary<string, object> a, string via)
        {
            if (_job != null) { d["ok"] = false; d["error"] = "a paste is running (" + _job.State + ")"; return d; }
            if (MySession.Static == null || MySector.MainCamera == null) { d["ok"] = false; d["error"] = "no session or camera"; return d; }
            int blocks = (int)Num(a, "blocks", 10000);
            if (blocks < 1) blocks = 1;
            if (blocks > S10Blueprint.MaxBlocks) blocks = S10Blueprint.MaxBlocks;
            double dist = Math.Max(20, Math.Min(2000, Num(a, "dist", 150)));
            object pv;
            string path = a.TryGetValue("path", out pv) && pv != null ? Convert.ToString(pv, CultureInfo.InvariantCulture) : "clipboard";
            if (path != "clipboard" && path != "direct") { d["ok"] = false; d["error"] = "path: clipboard|direct"; return d; }
            string why;
            MyObjectBuilder_CubeGrid ob = Builder(blocks, dist, out why);
            if (ob == null) { d["ok"] = false; d["error"] = why; return d; }
            var job = new PasteJob(ob, blocks, dist, path, via);
            _job = job;
            job.Begin();
            d["ok"] = job.State != "failed";
            d["paste"] = job.Describe();
            if (job.State == "failed") { d["error"] = job.Why; _last = job; _job = null; }
            return d;
        }

        private static Dictionary<string, object> Clear(Dictionary<string, object> d)
        {
            int closed = 0;
            foreach (PasteJob j in new[] { _job, _last })
            {
                if (j == null || j.Grid == null || j.Grid.Closed) continue;
                j.Grid.Close();
                closed++;
            }
            if (_job != null) _job.Stop("cleared");
            d["ok"] = true;
            d["closed"] = closed;
            DeLog.Info("DE.S10 clear: " + closed + " pasted grid(s) closed");
            return d;
        }

        /// <summary>The ship's build pass in front of the camera, centred <paramref name="dist"/> m along its forward.</summary>
        private static MyObjectBuilder_CubeGrid Builder(int blocks, double dist, out string why)
        {
            why = null;
            int kinds = S10Blueprint.Subtypes.Length;
            var defs = new MyCubeBlockDefinition[kinds];
            for (int k = 0; k < kinds; k++)
            {
                MyDefinitionId id;
                if (!MyDefinitionId.TryParse(S10Blueprint.Subtypes[k], out id) || !MyDefinitionManager.Static.TryGetCubeBlockDefinition(id, out defs[k]) || defs[k] == null)
                { why = "de.s10: no cube block definition " + S10Blueprint.Subtypes[k]; return null; }
            }
            MatrixD cam = MySector.MainCamera.WorldMatrix;
            Vector3D fwd = cam.Forward, up = cam.Up;
            var ob = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeGrid>();
            ob.EntityId = MyEntityIdentifier.AllocateId();
            ob.CreatePhysics = true;
            ob.GridSizeEnum = MyCubeSize.Large;
            ob.PersistentFlags = ob.PersistentFlags | MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.Enabled;
            ob.IsStatic = true; // a station: no physics settling of 10,000 blocks in the Water Mod's water during the measurement
            ob.DisplayName = "DE.S10 paste " + blocks + " " + DateTime.UtcNow.ToString("HHmmss", CultureInfo.InvariantCulture);
            ob.PositionAndOrientation = new MyPositionAndOrientation(cam.Translation + fwd * dist, (Vector3)fwd, (Vector3)up);
            foreach (S10Block b in S10Blueprint.Layout(blocks))
            {
                var cb = MyObjectBuilderSerializerKeen.CreateNewObject(defs[b.Kind].Id) as MyObjectBuilder_CubeBlock;
                if (cb == null) { why = "de.s10: " + defs[b.Kind].Id + " has no MyObjectBuilder_CubeBlock builder"; return null; }
                cb.Min = new SerializableVector3I(b.X, b.Y, b.Z);
                cb.EntityId = MyEntityIdentifier.AllocateId();
                cb.IntegrityPercent = 1f;
                cb.BuildPercent = 1f;
                ob.CubeBlocks.Add(cb);
            }
            return ob;
        }

        // ------------------------------------------------------------------------------------------ the paste job

        private sealed class PasteJob
        {
            private readonly MyObjectBuilder_CubeGrid _ob;
            private readonly string _via;
            public readonly int Blocks;
            public readonly double Dist;
            public string Path, State = "start", Why = "";
            public MyCubeGrid Grid;
            public double SelectMs = double.NaN, PlaceMs = double.NaN, FirstCallMs = double.NaN, SecondCallMs = double.NaN, WindowMaxMs;
            public int Tries, BlocksSeen;
            private long _t0, _tPlace, _tAdded;
            private ulong _stepStartMs, _previewSinceMs, _nextTryMs;
            private MyCubeGrid _added;

            public PasteJob(MyObjectBuilder_CubeGrid ob, int blocks, double dist, string path, string via)
            {
                _ob = ob;
                Blocks = blocks;
                Dist = dist;
                Path = path;
                _via = via;
            }

            private static double Ms(long a, long b) { return (b - a) * 1000.0 / Stopwatch.Frequency; }

            public void Begin()
            {
                MyEntities.OnEntityAdd += OnAdd;
                _stepStartMs = Win32.TickCount64();
                Row("start");
                if (Path == "direct") { Direct("requested"); return; }
                MyClipboardComponent c = MyClipboardComponent.Static;
                MyGridClipboard cb = c != null ? c.Clipboard : null;
                if (cb == null) { Direct("no clipboard component"); return; }
                cb.SetGridFromBuilders(new[] { _ob }, Vector3.Zero, (float)Dist);
                _t0 = Stopwatch.GetTimestamp();
                bool ok = c.Paste();
                FirstCallMs = Ms(_t0, Stopwatch.GetTimestamp());
                if (!ok) { Direct("Paste() refused the selection (copy-paste not allowed in this world?)"); return; }
                State = "selecting";
            }

            private void OnAdd(MyEntity e)
            {
                var g = e as MyCubeGrid;
                if (g == null || g.IsPreview || _added != null || g.DisplayName != _ob.DisplayName) return;
                _added = g;
                _tAdded = Stopwatch.GetTimestamp();
            }

            private void Direct(string why)
            {
                Path = "direct";
                Why = why;
                MyClipboardComponent c = MyClipboardComponent.Static;
                try { if (c != null && c.Clipboard != null && c.Clipboard.IsActive) c.Clipboard.Deactivate(); } catch (Exception) { }
                _tPlace = Stopwatch.GetTimestamp();
                MyEntities.CreateFromObjectBuilderParallel(_ob, true);
                SecondCallMs = Ms(_tPlace, Stopwatch.GetTimestamp());
                State = "placing";
                _stepStartMs = Win32.TickCount64();
                DeLog.Info("DE.S10 paste " + Blocks + " blocks: direct spawn (" + why + ")");
            }

            public void Tick(ulong now, bool inWorld)
            {
                if (!inWorld) { Stop("left the world"); return; }
                MyClipboardComponent c = MyClipboardComponent.Static;
                MyGridClipboard cb = c != null ? c.Clipboard : null;
                switch (State)
                {
                    case "selecting":
                        if (cb != null && cb.IsActive && cb.PreviewGrids.Count > 0)
                        {
                            SelectMs = Ms(_t0, Stopwatch.GetTimestamp());
                            State = "previewing";
                            _previewSinceMs = now;
                            Row("selected");
                        }
                        else if (now - _stepStartMs > StepTimeoutMs) Fail("no preview after " + StepTimeoutMs / 1000 + " s");
                        return;
                    case "previewing":
                        if (now - _previewSinceMs < PreviewSettleMs || now < _nextTryMs) return;
                        Tries++;
                        _tPlace = Stopwatch.GetTimestamp();
                        bool ok = c != null && c.Paste();
                        SecondCallMs = Ms(_tPlace, Stopwatch.GetTimestamp());
                        if (ok) { State = "placing"; _stepStartMs = now; return; } // placed (never pasted twice: a true answer ends the retries)
                        if (Tries >= MaxPasteTries) { Direct("the clipboard did not place after " + Tries + " tries (CanBePlaced or limits)"); return; }
                        _nextTryMs = now + RetryMs;
                        return;
                    case "placing":
                        if (_added != null)
                        {
                            Grid = _added;
                            Grid.Save = false;
                            PlaceMs = Ms(_tPlace, _tAdded);
                            BlocksSeen = Grid.BlocksCount;
                            Row("placed");
                            Finish("done");
                        }
                        else if (now - _stepStartMs > StepTimeoutMs) Fail("no grid '" + _ob.DisplayName + "' added after " + StepTimeoutMs / 1000 + " s");
                        return;
                }
            }

            private void Fail(string why) { Why = why; Finish("failed"); }

            public void Stop(string why) { if (State == "done" || State == "failed") return; Why = why; Finish("failed"); }

            private void Finish(string state)
            {
                State = state;
                MyEntities.OnEntityAdd -= OnAdd;
                Row(state);
                DeLog.Info("DE.S10 paste " + Blocks + " blocks " + state + " via " + Path + ": selectMs=" + N(SelectMs) + " placeMs=" + N(PlaceMs) +
                           " calls=" + N(FirstCallMs) + "/" + N(SecondCallMs) + " ms tries=" + Tries + " blocksSeen=" + BlocksSeen + (Why.Length > 0 ? " (" + Why + ")" : ""));
                if (_job == this) { _job = null; _last = this; }
            }

            private static string N(double v) { return double.IsNaN(v) ? "n/a" : v.ToString("0.#", CultureInfo.InvariantCulture); }

            private void Row(string phase)
            {
                Jsonl.WriteLine(S4Rows.Row(S10PerfRules.RowPaste, "phase", phase, "name", _ob.DisplayName, "blocks", Blocks, "dist", Dist, "path", Path,
                    "selectMs", SelectMs, "placeMs", PlaceMs, "firstCallMs", FirstCallMs, "secondCallMs", SecondCallMs, "tries", Tries,
                    "blocksSeen", BlocksSeen, "gridId", Grid != null ? (object)Grid.EntityId : null, "engineMaxMs", WindowMaxMs, "why", Why, "via", _via));
            }

            public Dictionary<string, object> Describe()
            {
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "state", State }, { "path", Path }, { "blocks", Blocks }, { "selectMs", SelectMs }, { "placeMs", PlaceMs },
                    { "firstCallMs", FirstCallMs }, { "secondCallMs", SecondCallMs }, { "tries", Tries }, { "blocksSeen", BlocksSeen },
                    { "gridId", Grid != null ? (object)Grid.EntityId : null }, { "why", Why },
                };
            }
        }
    }
}
