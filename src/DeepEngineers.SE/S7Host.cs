using System;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The S7 life-cycle entry points of the SE side that no build pass hook carries. The per-tick S7 work otherwise runs where its builders hooked it:
 /// the interior probe and the C1 bytes in <c>FrameWriter</c>, the RoomSet and the C2 lights/events in
 /// <c>GridMirrorProducer.S5</c>, the swim gate in <c>SwimController</c>, the audio muffle in <c>S2Host</c>, the
 /// bones in <c>CharacterExport</c>.
 /// <list type="bullet">
 /// <item><see cref="Tick"/>: the statics hosts (C12 drained, hosts built/closed), after <c>GridMirrorProducer.Tick</c>;</item>
 /// <item><see cref="OnWorldForgotten"/>: the S5 producer forgot the world (left it, or a new world epoch): the
 /// interior holds and room handlers, the effects' grid index;</item>
 /// <item><see cref="OnSessionUnload"/>: the session component's UnloadData: every statics host closed (never saved).</item>
 /// </list>
 /// Each call is guarded: an exception is logged and stays in its feature.
 /// </summary>
    internal static class S7Host
    {
        /// <summary>Per tick (update thread), after <c>GridMirrorProducer.Tick</c>. Never throws.</summary>
        public static void Tick(ulong nowMs, Link link, uint worldEpoch, bool inWorld)
        {
            S7Statics.Tick(nowMs, link, worldEpoch, inWorld); // guarded inside (missing member → statics off only)
        }

        /// <summary>The S5 producer's world forget (<c>GridMirrorProducer.S5Forget</c>).</summary>
        public static void OnWorldForgotten(string why)
        {
            try { S7Interior.OnWorldUnload(why); } catch (Exception e) { Error("interior", e); }
            try { InteriorRooms.OnWorldUnload(why); } catch (Exception e) { Error("rooms", e); }
            try { S7Effects.Forget(why); } catch (Exception e) { Error("fx", e); }
        }

        /// <summary>The session component's UnloadData: statics hosts closed, census index dropped.</summary>
        public static void OnSessionUnload()
        {
            try { S7Statics.OnUnload(); } catch (Exception e) { Error("statics", e); }
        }

        private static void Error(string where, Exception e)
        {
            DeLog.Info("DE.Error S7 host " + where + " " + e.GetType().Name + ": " + e.Message);
        }
    }
}
