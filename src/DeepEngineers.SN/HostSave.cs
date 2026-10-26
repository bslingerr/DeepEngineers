using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine.SceneManagement;
using UWE;

namespace DeepEngineers.SN
{
    internal enum HostSaveState
    {
        /// <summary>Waiting for the main menu (and its slot list).</summary>
        WaitingMenu = 0,
        /// <summary><c>DEEPENGINEERS_SN_AUTOLOAD=0</c>: the session stays at the main menu.</summary>
        AutoLoadOff = 1,
        /// <summary>Refused (logged `DE.HostSave REFUSE`): no load, or no ghost and no camera driving after the load.</summary>
        Refused = 2,
        /// <summary>`uGUI_MainMenu.LoadGameAsync` started (snPhase LoadingSave).</summary>
        Loading = 3,
        /// <summary>The Main scene is up with the Player, the streamer and the camera rig.</summary>
        WorldLoaded = 4,
    }

    /// <summary>
 /// The Subnautica host session (ROADMAP S1 SN plugin; CONTRACT §1 "Subnautica session", §7 "Start"; ARCHITECTURE
 /// §5.10): once the main menu is up, the host save is chosen through the menu's own slot API
 /// (<c>SaveLoadManager.main.GetActiveSlotNames</c> + <c>GetGameInfo(slot)</c>: session, gameMode, changeSet; filled by
 /// <c>StartScreen</c> before the main menu loads) and loaded exactly as the menu's Load button does
 /// (<c>CoroutineHost.StartCoroutine(uGUI_MainMenu.main.LoadGameAsync(slot, session, changeSet, gameMode))</c>; VERIFIED
 /// decomp: MainMenuLoadPanel, uGUI_MainMenu.LoadGameAsync, SaveLoadManager.LoadAsync). The load does not wait for SE:
 /// the launcher holds its token until ReadyIdle and starts SE only then (CONTRACT §7). Every Subnautica call runs in
 /// the caller's try/catch through a non-inlined method (a missing member fails that call only).
 /// </summary>
    internal sealed class HostSave
    {
        public const ulong MenuSettleMs = 1000;
        public const ulong LoadSlowWarnMs = 170000;
        public const ulong LoadFailCheckMs = 2000;
        public const string MainSceneName = "Main";

        private ulong _menuSinceMs, _loadStartMs;
        private bool _offLogged, _slowWarned, _sceneHooked, _mainSceneLoaded;

        public HostSaveState State { get; private set; }
        public HostSlotChoice Choice { get; private set; }
        public HostModeCheck Mode { get; private set; }
        public string RefuseReason { get; private set; }
        public ulong LoadMs { get; private set; }
        public int SlotCount { get; private set; }

        public void Attach()
        {
            if (_sceneHooked) return;
            SceneManager.sceneLoaded += OnSceneLoaded;
            _sceneHooked = true;
        }

        public void Detach()
        {
            if (!_sceneHooked) return;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _sceneHooked = false;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                if (scene.name == MainSceneName) _mainSceneLoaded = true;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
 /// One frame at snPhase Menu. Returns true on the frame the load starts (the caller sets LoadingSave). A refusal
 /// is logged once and leaves Subnautica at the main menu.
 /// </summary>
        public bool TickMenu(ulong now)
        {
            if (State == HostSaveState.Refused || State == HostSaveState.Loading || State == HostSaveState.WorldLoaded) return false;
            if (!S1Switches.AutoLoad)
            {
                State = HostSaveState.AutoLoadOff;
                if (!_offLogged) DeLog.Info("DE.HostSave autoload off (" + S1Switches.AutoLoadVar + "=0): Subnautica stays at the main menu");
                _offLogged = true;
                return false;
            }
            if (!S1Patches.SaveGuardMenu)
            {
                Refuse("save-guard-missing", "the IngameMenu.SaveGame prefix is not installed; the host save is never loaded without it");
                return false;
            }
            if (!MenuUp())
            {
                _menuSinceMs = 0;
                return false;
            }
            if (_menuSinceMs == 0) _menuSinceMs = now;
            if (now - _menuSinceMs < MenuSettleMs || SlotManagerBusy()) return false;

            List<HostSlot> slots = ReadSlots();
            SlotCount = slots.Count;
            foreach (HostSlot s in slots) DeLog.Info("DE.HostSave slot " + s);
            HostSlotChoice c = HostSaveRules.SelectSlot(slots, S1Switches.HostSession, S1Switches.HostSlot);
            Choice = c;
            if (!c.Ok)
            {

                // starts a new Freedom game through the main menu itself and writes the slot once the world is up (CreateSave);
 // DEEPENGINEERS_S9_HOSTSAVE=off keeps the refusal (the player makes the save by hand)
                if (CreateOn && !_createTried && HostSaveRules.NoFreedomSlot(slots))
                {
                    _createTried = true;
                    if (StartNewFreedom(now)) return true;
                }
                Refuse(c.Reason, c.Detail);
                return false;
            }
            S1Patches.SuppressUi = true;
            StartLoad(c.Slot);
            _loadStartMs = now;
            State = HostSaveState.Loading;
            DeLog.Info("DE.HostSave LOAD slot=" + c.Slot.Slot + " session=" + c.Slot.Session + " gameMode=" + LegacyGameMode.Name(c.Slot.GameMode) +
                       " changeSet=" + c.Slot.ChangeSet + " gameTime=" + c.Slot.GameTime + "s (" +
                       (S1Switches.HostSlot != null ? "slot named by " + S1Switches.HostSlotVar : "selected by session id, " + c.Matches + " match") +
                       ", " + slots.Count + " slots) via uGUI_MainMenu.LoadGameAsync; never saved (S1 save guards)");
            return true;
        }

        /// <summary>One frame at snPhase LoadingSave. Returns true on the frame the world is ready for the ghost.</summary>
        public bool TickLoading(ulong now)
        {
            if (State != HostSaveState.Loading) return false;
            if (WorldReady())
            {
                if (_creating && !CreateSave(now)) return false; // S9: the new game's world is up: the intro, then the one save
                State = HostSaveState.WorldLoaded;
                LoadMs = now - _loadStartMs;
                DeLog.Info("DE.HostSave world loaded in " + LoadMs + " ms (Main scene, Player, streamer ready, wait screen gone)" +
                           (_creating ? "; the host save was created and written once (slot " + (Choice != null ? Choice.Slot.Slot : "?") + ")" : ""));
                return true;
            }
            ulong waited = now - _loadStartMs;
            if (!_mainSceneLoaded && waited >= LoadFailCheckMs && MenuUp() && !SlotManagerBusy() && LoadErrorShown())
            {
                Refuse("load-failed", "LoadGameAsync failed: the main menu shows its load-error confirmation " + waited + " ms after the start");
                return false;
            }
            if (!_slowWarned && waited >= LoadSlowWarnMs)
            {
                _slowWarned = true;
                DeLog.Warn("DE.HostSave still loading after " + waited + " ms (the launcher waits 180 s for ReadyIdle)");
            }
            return false;
        }

        /// <summary>After the load: Freedom and not invisible (S1.N5), else REFUSE (no ghost, no camera driving).</summary>
        public bool CheckMode()
        {
            int mode, cheats;
            bool invisible;
            ReadMode(out mode, out cheats, out invisible);
            HostModeCheck m = HostSaveRules.CheckLoaded(mode, cheats, invisible);
            Mode = m;
            if (m.Ok)
            {
                DeLog.Info(m.Line + " slot=" + (Choice != null ? Choice.Slot.Slot : "?"));
                return true;
            }
            DeLog.Warn(m.Line + "; no ghost Player, no camera driving (Subnautica stays loaded, snPhase stays LoadingSave)");
            State = HostSaveState.Refused;
            RefuseReason = m.Reason;
            return false;
        }

        private void Refuse(string reason, string detail)
        {
            if (State == HostSaveState.Refused) return;
            State = HostSaveState.Refused;
            RefuseReason = reason;
            DeLog.Warn("DE.HostSave REFUSE " + reason + (detail != null ? " (" + detail + ")" : "") +
                       "; staying at the main menu, no camera driving");
        }

        public string Describe()
        {
            return "state=" + State + (RefuseReason != null ? " refuse=" + RefuseReason : "") +
                   (Choice != null && Choice.Ok ? " slot=" + Choice.Slot.Slot + " session=" + Choice.Slot.Session : "") +
                   (Mode != null ? " mode=" + (Mode.Ok ? "Freedom" : Mode.Reason) : "") + " loadMs=" + LoadMs;
        }

        // ------------------------------------------------------------------ Subnautica calls (non-inlined; the caller catches)

 // ------------------------------------------------------------------------------------ S9: creating the host save

 /// <summary>DEEPENGINEERS_S9_HOSTSAVE=off: never create a save.</summary>
        public static readonly bool CreateOn = ReadCreateOn();
        /// <summary>The intro cinematic is waited for this long at most before the save (the game's own lifepod intro takes about 2 min).</summary>
        public const ulong CreateIntroCapMs = 240000, CreateSaveSettleMs = 1500;
        private bool _createTried, _creating, _saveRequested;
        private ulong _createStartMs, _saveRequestMs;
        public bool Creating { get { return _creating; } }
        public string CreateNote = "";
        public static long Created;

        private static bool ReadCreateOn()
        {
            try { string v = Environment.GetEnvironmentVariable("DEEPENGINEERS_S9_HOSTSAVE"); return v == null || v.Trim().ToLowerInvariant() != "off"; }
            catch (Exception) { return true; }
        }

        /// <summary>
 /// The main menu's own Freedom button: <c>uGUI_MainMenu.OnButtonFreedom</c>, else <c>StartNewGame(GameMode.Freedom)</c>,
 /// found by reflection. True when the new game started (snPhase LoadingSave).
 /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool StartNewFreedom(ulong now)
        {
            try
            {
                uGUI_MainMenu menu = uGUI_MainMenu.main;
                if (menu == null) return false;
                Type t = menu.GetType();
                BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                MethodInfo m = t.GetMethod("OnButtonFreedom", all, null, Type.EmptyTypes, null);
                string how;
                if (m != null) { m.Invoke(menu, null); how = "uGUI_MainMenu.OnButtonFreedom()"; }
                else
                {
                    MethodInfo start = null;
                    foreach (MethodInfo mi in t.GetMethods(all))
                    {
                        if (mi.Name != "StartNewGame") continue;
                        ParameterInfo[] ps = mi.GetParameters();
                        if (ps.Length == 1 && ps[0].ParameterType.IsEnum) { start = mi; break; }
                    }
                    if (start == null) { CreateNote = "no OnButtonFreedom / StartNewGame(GameMode) on uGUI_MainMenu"; DeLog.Info("DE.HostSave CREATE refused: " + CreateNote); return false; }
                    object mode = Enum.ToObject(start.GetParameters()[0].ParameterType, LegacyGameMode.Freedom);
                    start.Invoke(menu, new[] { mode });
                    how = "uGUI_MainMenu.StartNewGame(" + mode + ")";
                }
                S1Patches.SuppressUi = true;
                _creating = true;
                _createStartMs = now;
                _loadStartMs = now;
                State = HostSaveState.Loading;
                Choice = new HostSlotChoice { Ok = true, Slot = new HostSlot { Slot = "(new)", Session = "(new)", GameMode = LegacyGameMode.Freedom }, Matches = 0 };
                CreateNote = "started via " + how;
                WriteCreatingMarker(true);
                DeLog.Info("DE.HostSave CREATE: no Freedom slot: a new Freedom game " + CreateNote + "; the slot is written once after the intro (never again)");
                return true;
            }
            catch (Exception e)
            {
                CreateNote = "failed: " + e.GetType().Name + ": " + (e.InnerException != null ? e.InnerException.Message : e.Message);
                DeLog.Info("DE.HostSave CREATE " + CreateNote);
                return false;
            }
        }

        /// <summary>
 /// After the new game's world is up: waits for the lifepod intro to end (the player free, or CreateIntroCapMs), then
 /// lets ONE save through the guards (<c>IngameMenu.SaveGame</c>, S1Patches.OneSaveWindow) and waits for the slot
 /// manager to finish; the Choice then carries the real slot and session. True once the save is written.
 /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool CreateSave(ulong now)
        {
            try
            {
                if (!_saveRequested)
                {
                    bool introDone = Player.main != null && !Player.main.cinematicModeActive;
                    bool capped = now - _createStartMs > CreateIntroCapMs;
                    if (!introDone && !capped) return false;
                    IngameMenu menu = IngameMenu.main;
                    if (menu == null) return false;
                    S1Patches.OneSaveWindow = true;
                    menu.SaveGame();
                    _saveRequested = true;
                    _saveRequestMs = now;
                    DeLog.Info("DE.HostSave CREATE save requested " + (introDone ? "after the intro" : "at the intro cap") + ", " + (now - _createStartMs) + " ms after the new game");
                    return false;
                }
                if (now - _saveRequestMs < CreateSaveSettleMs || SlotManagerBusy()) return false;
                SaveLoadManager m = SaveLoadManager.main;
                string slot = m != null ? m.GetCurrentSlot() : null;
                SaveLoadManager.GameInfo gi = slot != null && m != null ? m.GetGameInfo(slot) : null;
                Choice = new HostSlotChoice
                {
                    Ok = true, Matches = 1,
                    Slot = new HostSlot { Slot = slot ?? "(unknown)", Session = gi != null ? gi.session : "(unknown)", GameMode = gi != null ? (int)gi.gameMode : LegacyGameMode.Freedom,
                                          ChangeSet = gi != null ? gi.changeSet : 0, GameTime = gi != null ? gi.gameTime : 0, DateTicks = gi != null ? gi.dateTicks : 0 },
                };
                S1Patches.OneSaveWindow = false;
                Created++;
                WriteCreatingMarker(false);
                DeLog.Info("DE.HostSave CREATED slot=" + Choice.Slot.Slot + " session=" + Choice.Slot.Session + " gameMode=" + LegacyGameMode.Name(Choice.Slot.GameMode) +
                           " (saves allowed once: " + S1Patches.SavesAllowedOnce + "); from here it is the host save, never saved again");
                return true;
            }
            catch (Exception e)
            {
                S1Patches.OneSaveWindow = false;
                CreateNote = "save failed: " + e.GetType().Name + ": " + e.Message;
                DeLog.Info("DE.HostSave CREATE " + CreateNote + " (the world is used as it is this session; make the save by hand if it is missing next time)");
                WriteCreatingMarker(false);
                return true;
            }
        }

        /// <summary>%LOCALAPPDATA%\DeepEngineers\hostsave-creating.txt while the first save is being made: the launcher waits longer.</summary>
        private static void WriteCreatingMarker(bool on)
        {
            try
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(root)) return;
                string p = Path.Combine(Path.Combine(root, "DeepEngineers"), "hostsave-creating.txt");
                if (on) File.WriteAllText(p, DateTime.UtcNow.ToString("o") + " creating the Freedom host save (the intro runs once)\n");
                else if (File.Exists(p)) File.Delete(p);
            }
            catch (Exception) { }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool MenuUp()
        {
            return uGUI_MainMenu.main != null && SaveLoadManager.main != null;
        }

        /// <summary>LoadGameAsync's failure path shows <c>uGUI.main.confirmation</c> at the menu.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool LoadErrorShown()
        {
            uGUI ui = uGUI.main;
            return ui != null && ui.confirmation != null && ui.confirmation.gameObject.activeSelf;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool SlotManagerBusy()
        {
            SaveLoadManager m = SaveLoadManager.main;
            return m == null || m.isLoading || m.isSaving;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<HostSlot> ReadSlots()
        {
            var list = new List<HostSlot>();
            SaveLoadManager m = SaveLoadManager.main;
            string[] names = m.GetActiveSlotNames();
            for (int i = 0; i < names.Length; i++)
            {
                SaveLoadManager.GameInfo gi = m.GetGameInfo(names[i]);
                if (gi == null) continue;
                list.Add(new HostSlot
                {
                    Slot = names[i],
                    Session = gi.session,
                    GameMode = (int)gi.gameMode,
                    ChangeSet = gi.changeSet,
                    GameTime = gi.gameTime,
                    DateTicks = gi.dateTicks,
                    Corrupted = gi.corrupted,
                });
            }
            return list;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void StartLoad(HostSlot s)
        {
#pragma warning disable 618 // GameMode is marked obsolete, but it is LoadGameAsync's parameter type
            CoroutineHost.StartCoroutine(uGUI_MainMenu.main.LoadGameAsync(s.Slot, s.Session, s.ChangeSet, (GameMode)s.GameMode));
#pragma warning restore 618
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool WorldReady()
        {
            if (uGUI_MainMenu.main != null) return false;
            if (Player.main == null || LargeWorldStreamer.main == null || !LargeWorldStreamer.main.IsReady()) return false;
            if (WaitScreen.IsWaiting) return false;
            return MainCameraControl.main != null && SNCameraRoot.main != null && MainCamera.camera != null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadMode(out int mode, out int cheats, out bool invisible)
        {
            GameModeOption m, c;
            GameModeUtils.GetGameMode(out m, out c);
            mode = (int)m;
            cheats = (int)c;
            invisible = GameModeUtils.IsInvisible();
        }
    }
}
