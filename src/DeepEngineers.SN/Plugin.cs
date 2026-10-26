using System;
using BepInEx;

namespace DeepEngineers.SN
{
    /// <summary>
 /// BepInEx 5 entry point of the Subnautica side ("visual host"). BepInEx loads it in every Subnautica launch; it
 /// enters bridge mode only with the launcher's bridge token (CONTRACT §1.2, §6) and otherwise leaves Subnautica
 /// vanilla. Nautilus is a soft dependency for load order only (no reference; v1 does not use it).
 /// <para>Awake (main thread): `DE.Hello`, then <see cref="BridgeStartup.TryStart"/>: bridge token, mutex
 /// `Local\DeepEngineers_v1_sn`, window role (ghosting, power throttling, HWND), section creation by the creator
 /// rules, and the "DeepEngineers.Bridge" GameObject that runs every frame (<see cref="BridgeBehaviour"/>). The bridge
 /// object is separate from BepInEx's manager object and carries SceneCleanerPreserve, so it survives both scene
 /// loads and the game's SceneCleaner.</para>
 /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(NautilusGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "deepengineers.sn";
        public const string PluginName = "DeepEngineers.SN";
        public const string PluginVersion = "0.0.1";

        /// <summary>Nautilus 1.0.0-pre.54 `[BepInPlugin("com.snmodding.nautilus",...)]` (VERIFIED in the installed DLL).</summary>
        public const string NautilusGuid = "com.snmodding.nautilus";

        private bool _startAttempted;

        private void Awake()
        {
            DeLog.Init(Logger);
            try
            {
                DeLog.Info(HelloInfo.Line());
            }
            catch (Exception e)
            {
                // A type-load or static-initialiser failure must never break the game: stay dormant.
                DeLog.Error("DE.Bridge start-up exception, staying dormant: " + e);
                _startAttempted = true;
            }
        }

        /// <summary>
 /// The bridge GameObject is created on the first player-loop frame, not in Awake. Real run

 /// first scene load) was destroyed by the StartScreen scene load 8 s later without one Update, although
 /// DontDestroyOnLoad had been called. This plugin component lives on BepInEx's manager object, which survives.
 /// </summary>
        private void Update()
        {
            if (SnVersionGate.Refused) { SnVersionGate.TickNotice(); return; } // S9-version-gate: dormant, one notice
            if (_startAttempted) return;
            _startAttempted = true;
            try
            {
                DeLog.Info("DE.Bridge start on the first player-loop frame: frame=" + UnityEngine.Time.frameCount +
                           " activeScene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name +
                           " pluginScene=" + gameObject.scene.name);
                if (!SnVersionGate.CheckAtStart()) return; // S9-version-gate: another Subnautica build, bridge launch -> dormant
                BridgeStartup.TryStart();
            }
            catch (Exception e)
            {
                DeLog.Error("DE.Bridge start-up exception, staying dormant: " + e);
            }
        }
    }
}
