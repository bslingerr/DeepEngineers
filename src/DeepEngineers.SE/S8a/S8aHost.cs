using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using DeepEngineers.SE.Terrain;
using DeepEngineers.Wire;
using HarmonyLib;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The S8a life cycle on the SE side (ROADMAP "### S8a" build pass contract, "S8aHost (life cycle, per-tick calls,
 /// s8a.* rows)"; ARCHITECTURE §5.17). Everything is off unless <c>DEEPENGINEERS_S8A=on</c> (read at Init).
 /// <list type="bullet">
 /// <item><see cref="Init"/> (plugin Init): the environment switches, the feed's state, the C2 hook (<c>Link.C2Extra</c>:
 /// the S8a header and rows inside the C2 seqlock).</item>
 /// <item><see cref="ApplyPatches"/> (plugin ApplyHarmony): the <c>MySession.Save</c> postfix of the overlay.</item>
 /// <item><see cref="Tick"/> (plugin Update, after <c>S7Host.Tick</c>): a new feed stream when the tiles of a new world are
 /// hooked, the overlay's queue (dirty cells, mirrors, conversions), the C7 S8a block of the latest <c>SnStatus</c>
 /// (acks), the 1 Hz rows (<c>s8a.feed</c>, <c>s8a.perf</c>), the drill command's delayed witness.</item>
 /// <item>The unload path is <c>TerrainTiles.OnUnloadData → S8aOverlay.OnUnload</c> (the tiles own the session).</item>
 /// </list>
 /// Each call is guarded: an exception is logged (rate-limited) and stays in S8a.
 /// </summary>
    internal static unsafe class S8aHost
    {
        public const string EnvWorldMod = "DEEPENGINEERS_WORLDMOD";

        private static bool _inited;
        private static ulong _streamStoreKey;
        private static bool _streamUp, _windowUp;
        private static readonly RateLimit Errors = new RateLimit(5000);
        private static long _errors;
        private static readonly long[] _hookUs = new long[120];
        private static int _hookN;
        private static ulong _nextPerfMs;
        private static string _worldPath;

        public static bool Enabled { get { return S8aOverlay.Enabled; } }
        /// <summary>This world's store key ("store.key"), computed at world up for the window and the header (the overlay computes the same when drilling is on).</summary>
        public static ulong StoreKey { get; private set; }
        /// <summary>The deployed world mod's sculpt hash ("store.key": over the 12 planet data files), 0 when the folder is not found.</summary>
        public static ulong SculptHash { get; private set; }
        /// <summary>
 /// Planet v2 R8: the layout's runtime rules beside the planet data (<see cref="S8aPlanetRules.FileName"/>, written by the sculpt
 /// tool; the defaults when there is none: design 3), folded into the window's and the charts' keys and given to the crater's ore rule.
 /// </summary>
        public static S8aPlanetRules PlanetRules { get; private set; } = S8aPlanetRules.Defaults();
        public static string PlanetDataDir { get; private set; }

        /// <summary>Plugin Init (before the first world).</summary>
        public static void Init()
        {
            if (_inited) return;
            _inited = true;
            S8aOverlay.ReadEnv();
            S8aWindow.ReadEnv();
            S8aChartManager.ReadEnv();
            S8aRing.ReadEnv();
            if (!S8aOverlay.Enabled)
            {
                DeLog.Info("DE.S8a off (" + S8aOverlay.EnvS8a + " unset): S4's guards block every edit of the tiles; the C2 S8a header stays version 0");
                return;
            }
            S8aFeed.Init();
            Link.C2Extra = S8aFeed.WriteInto;
            DeLog.Info("DE.S8a on: conventions " + S8aConventions.Header + " " + S8aStore.Hex16(S8aConventions.Hash) +
                       (S8aConventions.Hash == S8aConventions.PinnedHash ? " (pinned)" : " (DIFFERS from the pin " + S8aStore.Hex16(S8aConventions.PinnedHash) + ")") +
                       " drill=" + (S8aOverlay.DrillSetting ? "on" : "off") + " (" + S8aOverlay.EnvDrill + ") window=" + (S8aWindow.Setting ? "on" : "off") + " (" + S8aWindow.EnvWindow + ") treadmill=" +
                       (S8aChartManager.TreadmillOn ? "on" : "off") + " (" + S8aChartManager.EnvTreadmill + ") ring=" + (S8aRing.Setting ? "on LOD<=" + S8aRing.MaxLod : "off") +
                       " (" + S8aRing.EnvRing + ") sample=" + (S8aSample.Setting ? "on" : "off") + " (" + S8aSample.EnvSample + ") store=" + S8aStore.DefaultRoot() +
                       " feed=C2+0x" + S8aConventions.C2S8a.Offset.ToString("x") + " ack=C7+0x" + S8aConventions.C7S8a.Offset.ToString("x"));
        }

        /// <summary>The world mod's planet data folder: <c>DEEPENGINEERS_WORLDMOD</c> (the mod folder) or the Mods folder's DeepEngineers.World.</summary>
 /// <summary>S8a Stage F2: the planet data folder for the ring field's PNG check (S8aRing, a pool thread; read only).</summary>
        internal static string FindPlanetDataDirPublic() { return FindPlanetDataDir(); }

        private static string FindPlanetDataDir()
        {
            var candidates = new List<string>();
            try { string env = Environment.GetEnvironmentVariable(EnvWorldMod); if (!string.IsNullOrEmpty(env)) candidates.Add(env); } catch (Exception) { }
            try { candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceEngineers", "Mods", "DeepEngineers.World")); } catch (Exception) { }
            foreach (string c in candidates)
            {
                string d = Path.Combine(c, "Data", "PlanetDataFiles", "DE_4546B");
                if (Directory.Exists(d)) return d;
            }
            return null;
        }

        private static void WorldUp(bool drillOn)
        {
            string path = null;
            try { path = Sandbox.Game.World.MySession.Static != null ? Sandbox.Game.World.MySession.Static.CurrentPath : null; } catch (Exception) { }
            _worldPath = path;
            StoreKey = !string.IsNullOrEmpty(path) ? S8aStore.StoreKey(path, SiteV1.SiteVersion) : 0;
            // one key per session: the drill overlay keyed the store at BeforeStart, and the C2 header Subnautica reads carries
 // that key. A new game from the Star System card moves CurrentPath from the card folder to the new save folder in
 // between
            ulong overlayKey = S8aOverlay.StoreKey;
            if (overlayKey != 0 && overlayKey != StoreKey)
            {
                DeLog.Info("DE.S8a world up: the session path moved (" + (path ?? "none") + "): the store key stays the drill overlay's " + S8aStore.Hex16(overlayKey) + " (not " + S8aStore.Hex16(StoreKey) + ")");
                StoreKey = overlayKey;
            }
            PlanetDataDir = FindPlanetDataDir();
            SculptHash = 0;
            if (PlanetDataDir != null) { try { SculptHash = S8aStore.SculptHashOfFolder(PlanetDataDir); } catch (Exception e) { DeLog.Info("DE.S8a sculpt hash not computed: " + e.GetType().Name + ": " + e.Message); } }
            string rulesError = null;
            try { PlanetRules = S8aPlanetRules.OfFolder(PlanetDataDir, out rulesError); }
            catch (Exception e) { PlanetRules = S8aPlanetRules.Defaults(); rulesError = e.GetType().Name + ": " + e.Message; }
            if (rulesError != null) DeLog.Info("DE.S8a WARN " + S8aPlanetRules.FileName + " not read (" + rulesError + "): today's rules");
            SculptHash = PlanetRules.FoldInto(SculptHash);
            ApplyPlanetRules();
            S8aHandDrill.Apply();   // the vein past the cover's edge)
 // Stage F2 (conventions "store.key"): with the ring field on, the window's and the charts' keys carry its identity, so a
 // window cache made without it (or with another library) is never served
            if (S8aRing.Setting && SculptHash != 0) SculptHash = S8aRing.FoldInto(SculptHash);
            // planet v2 route B (S8aSample): with the sample on the keys carry its identity too (a window cache made without it is never served)
            if (S8aSample.Setting && SculptHash != 0) SculptHash = S8aSample.FoldInto(SculptHash);
            S8aChartManager.OnWorldUp(SculptHash);
            if (S8aWindow.Setting && SiteBinding.Planet != null && SiteBinding.Map != null && StoreKey != 0)
            {
                S8aWindow.Start(S8aStore.DefaultRoot(), StoreKey, SculptHash, SiteBinding.Planet, SiteBinding.Map, S8aChartManager.ChartSeq, S8aChartManager.ChartKey);
                _windowUp = true;
            }
            DeLog.Info("DE.S8a world up: storeKey=" + S8aStore.Hex16(StoreKey) + " sculptHash=" + S8aStore.Hex16(SculptHash) + " planetData=" + (PlanetDataDir ?? "not found") +
                       " window=" + (_windowUp ? "started" : "off") + " drill=" + drillOn);
        }

        private static void WorldDown(string why)
        {
            if (_windowUp) { try { S8aWindow.Stop(); } catch (Exception) { } _windowUp = false; }
            S8aChartManager.OnWorldDown();
            S8aOutcropsSe.Reset();
        }

        /// <summary>
 /// Plugin ApplyHarmony: the overlay's save postfix (a missing member logs DE.S8a MISSING and turns only the persistence
 /// off). Installed whenever the plugin is present, S8a on or off: with S8a or drilling off the
 /// postfix carries an existing <c>DE_S8a_overlay.vx2</c> unchanged into every save, so SE's cleanup never drops it.
 /// </summary>
        public static void ApplyPatches()
        {
            var h = new Harmony(HarmonyPatches.HarmonyId);
            S8aOverlay.InstallSavePostfix(h);
            S8aRing.InstallPatches(h); // Stage F2: the ring field through the planet's generator (inert unless DEEPENGINEERS_S8A=on and DEEPENGINEERS_S8A_RING=on)
        }

        /// <summary>Per tick (update thread), after <c>S7Host.Tick</c>. Never throws.</summary>
        public static void Tick(ulong nowMs, Link link, bool inWorld)
        {
            if (!S8aOverlay.Enabled) return;
            long t0 = Stopwatch.GetTimestamp();
            S10Se.S8aStart(); // S10 hook (round 3 F): the steps below on a lap clock (s10.se s8aSteps), timestamps only
            try
            {
                // the stream (and the window, the charts) follow the bound site: drilling on, or the window alone
                bool siteUp = inWorld && SiteBinding.IsOk && SiteBinding.Planet != null;
                bool hooked = inWorld && (S8aOverlay.DrillOn || (siteUp && S8aWindow.Setting));
                if (hooked && !_streamUp)
                {
                    WorldUp(S8aOverlay.DrillOn);
                    S8aFeed.NewStream("world up");
                    _streamStoreKey = StoreKey;
                    _streamUp = true;
                }
                else if (hooked && _streamUp && StoreKey != 0 && _streamStoreKey != StoreKey)
                {
                    WorldDown("another world");
                    WorldUp(S8aOverlay.DrillOn);
                    S8aFeed.NewStream("another world");
                    _streamStoreKey = StoreKey;
                }
                else if (!inWorld && _streamUp)
                {
                    WorldDown("left the world");
                    S8aFeed.Close("left the world");
                    _streamUp = false;
                }
                if (_streamUp && !_windowUp && siteUp && S8aWindow.Setting && SiteBinding.Map != null && StoreKey != 0)
                {
                    S8aWindow.Start(S8aStore.DefaultRoot(), StoreKey, SculptHash, SiteBinding.Planet, SiteBinding.Map, S8aChartManager.ChartSeq, S8aChartManager.ChartKey);
                    _windowUp = true;
                }
                Vec3 camSe;
                bool haveCam = Camera(out camSe);
                S10Se.S8aLap(S10TerrainSlice.SeStream); // S10 hook (round 3 F): lap mark
                S8aOverlay.Tick(nowMs);
                S8aDrillConverter.Tick(nowMs);
                S10Se.S8aLap(S10TerrainSlice.SeOverlay); // S10 hook (round 3 F): lap mark
                if (_streamUp && inWorld) S8aOreOverlaySe.Tick(nowMs, camSe, haveCam); // the crater's ore overlay: batches near the camera painted over their veins
                S10Se.S8aLap(S10TerrainSlice.SeOreOverlay); // S10 hook (round 3 F): lap mark
                S8aDrillConverter.TickMirrors(4); // game thread: the tile-overlap mirror writes (Havok invalidation)
                S10Se.S8aLap(S10TerrainSlice.SeMirrors); // S10 hook (round 3 F): lap mark
                if (link != null && link.HasSnStatus) S8aFeed.OnC7(link.LastSnStatus);
                S10Se.S8aLap(S10TerrainSlice.SeFeed); // S10 hook (round 3 F): lap mark
                if (_streamUp && inWorld)
                {
                    S8aChartManager.Tick(nowMs, camSe, haveCam);
                    S10Se.S8aLap(S10TerrainSlice.SeCharts); // S10 hook (round 3 F): lap mark
                    S8aWindow.Tick(nowMs, camSe, haveCam);
                    S10Se.S8aLap(S10TerrainSlice.SeWindow); // S10 hook (round 3 F): lap mark
                }
                S8aFeed.Rows(nowMs);
                S10Se.S8aLap(S10TerrainSlice.SeRows); // S10 hook (round 3 F): lap mark
                if (_streamUp && inWorld) S8aCraterOresSe.Tick(nowMs); // Stage E: Subnautica's large deposits -> the tiles' ore pockets
                S10Se.S8aLap(S10TerrainSlice.SeCrater); // S10 hook (round 3 F): lap mark
                if (_streamUp && inWorld) S8aOutcropsSe.Tick(nowMs, camSe, haveCam, StoreKey); // planet v2 R8: SE's outcrops -> Subnautica's deposits
                S10Se.S8aLap(S10TerrainSlice.SeOutcrops); // S10 hook (round 3 F): lap mark
                S8aCommands.Tick(nowMs, inWorld);
                S8aRing.Tick();
                S10Se.S8aLap(S10TerrainSlice.SeCommands); // S10 hook (round 3 F): lap mark
                if (_streamUp && inWorld) S8aSample.Tick(nowMs, camSe, haveCam, StoreKey); // planet v2 route B: the sample's init line and its entity file
                S10Se.S8aLap(S10TerrainSlice.SeSample); // S10 hook (round 3 F): lap mark
            }
            catch (Exception e)
            {
                _errors++;
                if (Errors.Allow(nowMs)) DeLog.Info("DE.Error S8a host #" + _errors + " " + e.GetType().Name + ": " + e.Message);
            }
            long us = (Stopwatch.GetTimestamp() - t0) * 1000000L / Stopwatch.Frequency;
            _hookUs[_hookN % _hookUs.Length] = us;
            _hookN++;
            if (nowMs >= _nextPerfMs)
            {
                _nextPerfMs = nowMs + 1000;
                if (inWorld) Jsonl.WriteLine(S4Rows.Row("s8a.perf", "tick", (long)GameState.GameplayTick, "hookUsP99", HookUsP99(), "hookUsMax", HookUsMax(), "errors", _errors));
            }
        }

        /// <summary>The main camera's SE position (<c>MySector.MainCamera.Position</c>), false when there is none.</summary>
        public static bool Camera(out Vec3 p)
        {
            p = Vec3.Zero;
            try
            {
                var cam = Sandbox.Game.World.MySector.MainCamera;
                if (cam == null) return false;
                VRageMath.Vector3D c = cam.Position;
                p = new Vec3(c.X, c.Y, c.Z);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>The 99th percentile of the last 120 ticks' S8a host time (µs; the game-thread budget is 20 µs per guard call, the host's drain is bounded by the queue).</summary>
        public static long HookUsP99()
        {
            int n = Math.Min(_hookN, _hookUs.Length);
            if (n == 0) return 0;
            var a = new long[n];
            Array.Copy(_hookUs, a, n);
            Array.Sort(a);
            int i = (int)Math.Ceiling(n * 0.99) - 1;
            return a[Math.Max(0, Math.Min(n - 1, i))];
        }

        public static long HookUsMax()
        {
            int n = Math.Min(_hookN, _hookUs.Length);
            long m = 0;
            for (int i = 0; i < n; i++) if (_hookUs[i] > m) m = _hookUs[i];
            return m;
        }

        /// <summary>The crater's ore rule takes the planet rules (cube rule, deposit cap); its deposits are read again at the next poll.</summary>
        internal static void ApplyPlanetRules()
        {
            S8aCraterOreRule rule = TerrainTiles.CraterOres;
            if (rule == null) return;
            bool cube = PlanetRules.CraterCubeRule || S8aMutants.M27;
            if (rule.CubeRule == cube && rule.DepositsPerKm2 == PlanetRules.CraterDepositsPerKm2) return;
            rule.CubeRule = cube;
            rule.DepositsPerKm2 = PlanetRules.CraterDepositsPerKm2;
            S8aCraterOresSe.ForceReload();
            DeLog.Info("DE.S8a planet rules: " + PlanetRules);
        }

        public static Dictionary<string, object> Status()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "enabled", S8aOverlay.Enabled }, { "conventions", S8aConventions.Header }, { "hash", S8aStore.Hex16(S8aConventions.Hash) },
                { "pinned", S8aConventions.Hash == S8aConventions.PinnedHash }, { "streamUp", _streamUp }, { "errors", _errors }, { "hookUsP99", HookUsP99() },
                { "storeKey", S8aStore.Hex16(StoreKey) }, { "sculptHash", S8aStore.Hex16(SculptHash) }, { "planetData", PlanetDataDir }, { "planetRules", PlanetRules.ToString() },
                { "overlay", S8aOverlay.Status() }, { "converter", S8aDrillConverter.Status() }, { "feed", S8aFeed.Status() }, { "window", S8aWindow.Status() },
                { "chart", S8aChartManager.Status() }, { "mutants", S8aMutants.Active }, { "ring", S8aRing.Status() }, { "sample", S8aSample.Status() },
            };
        }
    }
}
