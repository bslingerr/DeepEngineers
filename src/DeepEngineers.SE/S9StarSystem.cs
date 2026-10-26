using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using VRage.FileSystem;

namespace DeepEngineers.SE
{
    /// <summary>

 /// New Game carousel is a fixed list (<c>MyGuiScreenSimpleNewGame.RecreateControls</c>) and every card starts through
 /// <c>StartWorld(sessionPath, gameMode, onlineMode)</c>, which loads checkpoint, sector and voxel storages from that one
 /// folder. This prefix swaps the Star System folder for ours (SE's own eight planets plus 4546B and its capsule spawn):
 /// <c>%LOCALAPPDATA%\DeepEngineers\worlds\DeepEngineers 4546B</c> first (the launcher's copy, nothing in the install),
 /// else <c>Content\CustomWorlds\DeepEngineers 4546B</c>. Only our launcher loads this plugin, so SE started from Steam
 /// keeps its own Star System. <c>DEEPENGINEERS_STARSYSTEM=off</c> keeps SE's folder; the Customize path is not covered
 /// (there the world is listed by name when the CustomWorlds copy exists). First written 21:1x, removed 21:5x for the
 /// Workshop route, back as a switch for the installer's end goal.
 /// </summary>
    internal static class S9StarSystem
    {
        public const string Env = "DEEPENGINEERS_STARSYSTEM", WorldName = "DeepEngineers 4546B", CardFolder = "Star System";
        public static bool Off = ReadOff();
        public static string Last = "none";
        public static long Swaps;

        public static int Patched;

        /// <summary>
 /// the log had the patch but no swap line): the New Game
 /// screen of today's SE is <c>MyGuiScreenNewGame.StartSelectedWorld</c>, not the simple carousel, and both go through
 /// <c>MySessionLoader.LoadSingleplayerSession(string sessionDirectory,...)</c>; the simple screen also has two
 /// <c>StartWorld</c> overloads (bool preferOnline / MyOnlineModeEnum). All of them are patched; the swap needs the
 /// folder to be <c>...\CustomWorlds\Star System</c>, so a player's own save of that name (under Saves) is never touched.
 /// </summary>
        public static void Install()
        {
            if (Off) { DeLog.Info("DE.S9 Star System card swap off (" + Env + "=off): SE's own Star System"); return; }
            // \CustomWorlds\Star System" with none of
 // the three first patches hit: today's New Game screen (MyGuiScreenNewGame.StartSelectedWorld) reads the checkpoint itself
 // (MyLocalCache.LoadCheckpoint(sessionDirectory, out size, forceGameMode, forceOnlineMode)) and hands it to the checkpoint
 // overload LoadSingleplayerSession(checkpoint, sessionPath, size, afterLoad) -> MySession.Load(sessionPath, checkpoint,...).
 // So every method on the way that takes the session folder is patched, the path parameter found by position (0 or 1).
            var harmony = new Harmony(HarmonyPatches.HarmonyId);
            var prefix0 = new HarmonyMethod(typeof(S9StarSystem).GetMethod(nameof(SessionPathPrefix0), BindingFlags.Static | BindingFlags.NonPublic));
            var prefix1 = new HarmonyMethod(typeof(S9StarSystem).GetMethod(nameof(SessionPathPrefix1), BindingFlags.Static | BindingFlags.NonPublic));
            string[][] targets = { new[] { "MyGuiScreenSimpleNewGame", "StartWorld" }, new[] { "MySessionLoader", "LoadSingleplayerSession" },
                                   new[] { "MyLocalCache", "LoadCheckpoint" }, new[] { "MyLocalCache", "LoadSector" }, new[] { "MySession", "Load" } };
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = a.GetTypes(); } catch (Exception) { continue; }
                foreach (Type t in types)
                {
                    string want = null;
                    foreach (string[] tg in targets) if (tg[0] == t.Name) { want = tg[1]; break; }
                    if (want == null) continue;
                    foreach (MethodInfo mi in t.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (mi.Name != want) continue;
                        ParameterInfo[] ps = mi.GetParameters();
                        int idx = -1;
                        for (int i = 0; i < ps.Length && i < 2; i++) if (ps[i].ParameterType == typeof(string) && !ps[i].IsOut) { idx = i; break; }
                        if (idx < 0) continue;
                        try
                        {
                            harmony.Patch(mi, prefix: idx == 0 ? prefix0 : prefix1);
                            Patched++;
                            DeLog.Info("DE.Harmony patched " + t.Name + "." + mi.Name + "(" + ps.Length + " params, path '" + ps[idx].Name + "' at " + idx + ") prefix (S9: the Star System scenario folder becomes '" + WorldName + "' when its folder exists; " + Env + "=off keeps SE's)");
                        }
                        catch (Exception e) { DeLog.Info("DE.Harmony S9 patch of " + t.Name + "." + mi.Name + " FAIL " + e.GetType().Name + ": " + e.Message); }
                    }
                }
            }
            if (Patched == 0) DeLog.Info("DE.Harmony MISSING the session loaders (S9 card swap): feature off");
        }

        private static bool _missingLogged;

        /// <summary>
 /// Our world's folder: <c>&lt;LocalApplicationData&gt;\DeepEngineers\worlds\DeepEngineers 4546B</c> (the launcher's copy),
 /// else the CustomWorlds copy; null when neither holds a Sandbox.sbc. 23:5x: inside SE <c>LOCALAPPDATA</c> read as an
 /// environment variable came back empty (the log said "no folder" while the file was there), so the folder comes from
 /// <see cref="Environment.GetFolderPath"/> as the plugin's own logger does, the variable and USERPROFILE as fallbacks;
 /// the candidates tried are logged once when none holds the world.
 /// </summary>
        public static string OurWorld()
        {
            var tried = new System.Collections.Generic.List<string>();
            try
            {
                string[] roots =
                {
                    SafeFolder(Environment.SpecialFolder.LocalApplicationData),
                    Environment.GetEnvironmentVariable("LOCALAPPDATA"),
                    JoinSafe(Environment.GetEnvironmentVariable("USERPROFILE"), "AppData", "Local"),
                    JoinSafe(SafeFolder(Environment.SpecialFolder.UserProfile), "AppData", "Local"),
                };
                foreach (string r in roots)
                {
                    if (string.IsNullOrEmpty(r)) continue;
                    string p = JoinSafe(r, "DeepEngineers", "worlds", WorldName);
                    if (p == null) continue;
                    tried.Add(p);
                    if (HoldsWorld(p)) return p;
                }
                string c = JoinSafe(MyFileSystem.ContentPath, "CustomWorlds", WorldName);
                if (c != null)
                {
                    tried.Add(c);
                    if (HoldsWorld(c)) return c;
                }

                // 22:49 sessions loaded it), under every Steam id folder of SE's own user data path
                string saves = JoinSafe(MyFileSystem.UserDataPath, "Saves");
                if (saves != null && Directory.Exists(saves))
                    foreach (string sid in Directory.GetDirectories(saves))
                    {
                        string w = Path.Combine(sid, WorldName);
                        tried.Add(w);
                        if (HoldsWorld(w)) return w;
                    }
            }
            catch (Exception e) { tried.Add("error " + e.GetType().Name + ": " + e.Message); }
            if (!_missingLogged) { _missingLogged = true; DeLog.Info("DE.S9 no '" + WorldName + "' world found; tried: " + string.Join(" | ", tried.ToArray())); }
            return null;
        }

        private static int _probes;

        /// <summary>
 /// 23:5x: inside SE <c>File.Exists</c> answered false for a file that is there (PowerShell: exists, 28,263 bytes, ROOT
 /// FullControl), so the folder is accepted when any of four checks finds Sandbox.sbc, and the first probes log each
 /// check's answer (File.Exists, FileInfo.Exists, Directory.GetFiles, File.GetAttributes) to name the one that lies.
 /// </summary>
        private static bool HoldsWorld(string folder)
        {
            string file = Path.Combine(folder, "Sandbox.sbc");
            bool a = false, b = false, c = false, d = false;
            string why = "";
            try { a = File.Exists(file); } catch (Exception e) { why += " File.Exists:" + e.GetType().Name; }
            try { b = new FileInfo(file).Exists; } catch (Exception e) { why += " FileInfo:" + e.GetType().Name; }
            try { c = Directory.Exists(folder) && Directory.GetFiles(folder, "Sandbox.sbc").Length > 0; } catch (Exception e) { why += " GetFiles:" + e.GetType().Name; }
            try { File.GetAttributes(file); d = true; } catch (Exception e) { why += " GetAttributes:" + e.GetType().Name; }
            if (_probes < 4) { _probes++; DeLog.Info("DE.S9 world probe '" + file + "': File.Exists=" + a + " FileInfo=" + b + " GetFiles=" + c + " GetAttributes=" + d + (why.Length > 0 ? " errors:" + why : "")); }
            return a || b || c || d;
        }

        private static string SafeFolder(Environment.SpecialFolder f)
        {
            try { return Environment.GetFolderPath(f); } catch (Exception) { return null; }
        }

        private static string JoinSafe(string root, params string[] parts)
        {
            try
            {
                if (string.IsNullOrEmpty(root)) return null;
                string p = root;
                foreach (string x in parts) p = Path.Combine(p, x);
                return p;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Is this SE's Star System scenario folder (a CustomWorlds child named "Star System")?</summary>
        public static bool IsStarSystemScenario(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string p = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string leaf = Path.GetFileName(p);
            if (!string.Equals(leaf, CardFolder, StringComparison.OrdinalIgnoreCase)) return false;
            string parent = Path.GetFileName(Path.GetDirectoryName(p) ?? "");
            return string.Equals(parent, "CustomWorlds", StringComparison.OrdinalIgnoreCase);
        }

        private static int _seen;

        private static void SessionPathPrefix0(ref string __0) { Swap(ref __0); }
        private static void SessionPathPrefix1(ref string __1) { Swap(ref __1); }

        private static void Swap(ref string path)
        {
            try
            {
                if (_seen < 12 && !string.IsNullOrEmpty(path)) { _seen++; DeLog.Info("DE.S9 session path seen: '" + path + "'" + (IsStarSystemScenario(path) ? " (SE's Star System scenario)" : "")); }
                if (!IsStarSystemScenario(path)) return;
                string ours = OurWorld();
                if (ours == null) { Last = "Star System kept: no '" + WorldName + "' folder"; DeLog.Info("DE.S9 Star System card -> SE's own (" + Last + ")"); return; }
                if (string.Equals(path, ours, StringComparison.OrdinalIgnoreCase)) return;
                path = ours;
                Swaps++;
                Last = "swapped to " + ours;
                DeLog.Info("DE.S9 Star System card -> '" + ours + "' (SE's planets plus 4546B and its capsule spawn)");
                EnsureSectorCache(ours);
            }
            catch (Exception e) { Last = "failed: " + e.GetType().Name; DeLog.Info("DE.S9 Star System card swap failed: " + e.GetType().Name + ": " + e.Message + " (SE's own folder)"); }
        }

        /// <summary>
 /// laptop SE log 18:35:31: "Incorrect save data"
 /// MyLoadingNeedXMLException, UnloadOnException, then a second load, 70 s behind the "Unloading world" screen): SE's
 /// first load of a world reads only the binary sector SANDBOX_0_0_0_.sbsB5 (MyLocalCache.LoadSector, allowXml false); a
 /// card without it fails, SE unloads, asks to convert from XML and loads again. The card ships without it (it is the
 /// game's own serialization), so the first pick of the card has SE write it now, through the same call with allowXml
 /// true (it deserializes the XML sector and writes the.sbsB5 next to it). Once per card folder; never throws.
 /// </summary>
        private static void EnsureSectorCache(string world)
        {
            try
            {
                string xml = Path.Combine(world, "SANDBOX_0_0_0_.sbs");
                if (File.Exists(xml + "B5") || !File.Exists(xml)) return;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                ulong size;
                bool needsXml;
                var sector = Sandbox.Engine.Networking.MyLocalCache.LoadSector(world, VRageMath.Vector3I.Zero, true, out size, out needsXml);
                SectorCacheWrites++;
                DeLog.Info("DE.S9 Star System card: binary sector written by SE in " + sw.ElapsedMilliseconds + " ms (" + (sector != null ? "ok" : "the XML sector did not load") +
                           ", " + (File.Exists(xml + "B5") ? "SANDBOX_0_0_0_.sbsB5 present" : "no .sbsB5") + "): the load goes straight in, no XML conversion");
            }
            catch (Exception e) { DeLog.Info("DE.S9 Star System card: binary sector not written (" + e.GetType().Name + ": " + e.Message + "): SE converts from XML on this load"); }
        }

        public static long SectorCacheWrites;

        private static bool ReadOff()
        {
            try { string v = Environment.GetEnvironmentVariable(Env); return v != null && v.Trim().ToLowerInvariant() == "off"; }
            catch (Exception) { return false; }
        }
    }
}
