using System;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game;

namespace DeepEngineers.SE
{
    internal enum GateState
    {
        /// <summary>No session (main menu, loading).</summary>
        NoSession = 0,
        Open = 1,
        Refused = 2,
    }

    /// <summary>
 /// SE global activation gate (CONTRACT §1.2), S0 part: "offline single player"; a multiplayer session leaves the
 /// plugin inert (`DE.Gate REFUSE multiplayer`, CONTRACT §7). The "exactly one DE_4546B planet" half needs the site
 /// binding and arrives with S1. Facts used, VERIFIED
 /// `Sandbox.Engine.Platform.Game.IsDedicated` (static field), `Sync.MultiplayerActive` (= `MyMultiplayer.Static != null`),
 /// `Sync.IsServer`, `MySession.OnlineMode` (= `Settings.OnlineMode`, `MyOnlineModeEnum` OFFLINE/PUBLIC/FRIENDS/PRIVATE).
 /// A world whose online mode is not OFFLINE is refused even with nobody else connected: SE opens a lobby for it.
 /// Evaluated once per world epoch on the update thread, when the world is in play.
 /// </summary>
    internal static class ActivationGate
    {
        public static GateState State { get; private set; }
        public static string Reason { get; private set; }
        private static uint _evaluatedEpoch = uint.MaxValue;

        public static bool IsOpen { get { return State == GateState.Open; } }

        /// <summary>Back to "no session" (menu).</summary>
        public static void Clear()
        {
            State = GateState.NoSession;
            Reason = "no session";
            _evaluatedEpoch = uint.MaxValue;
        }

        /// <summary>Evaluates the gate for this world epoch (first InWorld tick); logs `DE.Gate OPEN|REFUSE` once.</summary>
        public static void EvaluateOnce(uint worldEpoch)
        {
            if (_evaluatedEpoch == worldEpoch && State != GateState.NoSession) return;
            _evaluatedEpoch = worldEpoch;
            string why;
            bool open = Evaluate(out why);
            State = open ? GateState.Open : GateState.Refused;
            Reason = why;
            DeLog.Info(open
                ? "DE.Gate OPEN " + why + " worldEpoch=" + worldEpoch + " (S0: planet check DE_4546B arrives with the site binding, S1)"
                : "DE.Gate REFUSE multiplayer " + why + " worldEpoch=" + worldEpoch + " (plugin inert for this session: no hello, gateOpen 0)");
        }

        /// <summary>
 /// S4 (CONTRACT §1.2 "SE terrain" condition 1): the same predicate, evaluated early (the session component's
 /// BeforeStart, before the first InWorld tick) and side-effect free (no state, no log).
 /// </summary>
        public static bool Peek(out string why)
        {
            return Evaluate(out why);
        }

        private static bool Evaluate(out string why)
        {
            try
            {
                MySession s = MySession.Static;
                if (s == null) { why = "no session"; return false; }
                bool dedicated = Sandbox.Engine.Platform.Game.IsDedicated;
                bool mpActive = Sync.MultiplayerActive;
                bool isServer = Sync.IsServer;
                MyOnlineModeEnum mode = s.OnlineMode;
                string facts = "onlineMode=" + mode + " multiplayerActive=" + mpActive + " isServer=" + isServer + " dedicated=" + dedicated;
                bool offlineSp = !dedicated && !mpActive && isServer && mode == MyOnlineModeEnum.OFFLINE && MyMultiplayer.Static == null;
                why = (offlineSp ? "offline single player " : "") + facts;
                return offlineSp;
            }
            catch (Exception e)
            {
                why = "error " + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }
    }
}
