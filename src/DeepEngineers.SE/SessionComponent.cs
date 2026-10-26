using System;
using VRage.Game.Components;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The plugin's session component (ROADMAP S1 "SE plugin"). Pulsar registers every
 /// <c>[MySessionComponentDescriptor]</c> type of our main assembly when SE registers the game assembly's components
 /// which <c>MySession.RegisterComponentsFromAssemblies</c> does after
 /// the mod script assemblies. Every <c>LoadData</c> runs
 /// in <c>PrepareBaseSession → LoadDataComponents</c>, before <c>LoadWorld → MyEntities.Load</c> and before
 /// <c>BeforeStartComponents</c>, where the Water Mod sends its API dictionary.
 /// <para>Public with a parameterless constructor (Pulsar uses <c>Activator.CreateInstance</c>). Inert when the plugin
 /// is (dedicated server, measure-only mode, Init failure). Never throws into SE.</para>
 /// </summary>
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public sealed class DeepEngineersSession : MySessionComponentBase
    {
        private static RateLimit _errors = new RateLimit(10000);

        private static bool Active
        {
            get
            {
                Plugin p = Plugin.Instance;
                return p != null && p.S1Active;
            }
        }

        public override void LoadData()
        {
            if (!Active) return;
            try { SiteBinding.OnLoadData(); }
            catch (Exception e) { Error("LoadData", e); }
        }

        public override void BeforeStart()
        {
            if (!Active) return;
            try { SiteBinding.OnBeforeStart(); }
            catch (Exception e) { Error("BeforeStart", e); }
            // S4: the 34 terrain tiles right after the planet search (C first; one component, fixed order; CONTRACT §1.2)
            try { Terrain.TerrainTiles.OnBeforeStart(); }
            catch (Exception e) { Error("BeforeStart terrain", e); }
        }

        public override void UpdateBeforeSimulation()
        {
            if (!Active) return;
            try { SiteBinding.OnUpdateBeforeSimulation(); }
            catch (Exception e) { Error("UpdateBeforeSimulation", e); }
            try { Terrain.TerrainTiles.OnUpdateBeforeSimulation(); } // S4: DE.Terrain STATS every 10 s, the fail-safe's HUD
            catch (Exception e) { Error("UpdateBeforeSimulation terrain", e); }
            VersionGate.TickHud(); // S9-version-gate: the one HUD notice of a refused game version (never throws)
        }

        /// </summary>
        public override void Draw()
        {
            if (!Active) return;
            try { CrossingFlash.Draw(); }
            catch (Exception e) { Error("Draw flash", e); }
            S10Se.DrawCurtain(); // S10 hook (round 2 B): the loading curtain over SE's picture under the switch altitude (never throws)
        }

        protected override void UnloadData()
        {
            try { SiteBinding.OnUnloadData(); }
            catch (Exception e) { Error("UnloadData", e); }
            try { Terrain.S4Harness.OnUnload(); Terrain.TerrainTiles.OnUnloadData(); } // S4
            catch (Exception e) { Error("UnloadData terrain", e); }
            S7Host.OnSessionUnload(); // S7: statics hosts closed (never saved; guarded)
        }

        private static void Error(string where, Exception e)
        {
            if (_errors.Allow(Wire.Native.Win32.TickCount64()))
                DeLog.Info("DE.Error session " + where + " " + e.GetType().Name + ": " + e.Message);
        }
    }
}
