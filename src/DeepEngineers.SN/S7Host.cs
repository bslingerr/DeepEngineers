using System;
using DeepEngineers.Wire;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The S7 host of the Subnautica side for the features that are not hosted elsewhere. The interiors and lights run in <see cref="InteriorHost"/> and the effects in
 /// <see cref="S7Effects"/> (both wired by their builders into the bridge); this host carries the rest:
 /// <list type="bullet">
 /// <item><see cref="S7Statics"/>: the C12 statics reporter (needs only the mapped section; C7 capabilities bit 2,
 /// <c>de.statics</c>);</item>
 /// <item><c>de.bones</c>: the skinned astronaut's C11 verb (<see cref="SkinnedAstronaut.Handle"/>; the skin itself
 /// runs inside each <see cref="CharacterMirror"/>).</item>
 /// </list>
 /// Created by the bridge after the other hosts; disposed before the section is unmapped (the C12 producer writes into
 /// the view). An exception in the statics reporter turns the reporter off only; the host never takes S0-S7a down.
 /// </summary>
    internal sealed unsafe class S7Host : IDisposable
    {
        private S7Statics _statics;
        private bool _disposed;

        /// <param name="sectionBase">the mapped bridge section (C12 producer)</param>
 /// <param name="generation">this Subnautica's generation (statics epochs restart with it)</param>
 /// <param name="proxySource">review F17: the S6 proxy sections for the statics reporter's insideProxy count (null without S6)</param>
        public S7Host(byte* sectionBase, uint generation, Func<System.Collections.Generic.List<ProxySection>> proxySource = null)
        {
            try { _statics = new S7Statics(sectionBase, generation, proxySource); }
            catch (Exception e)
            {
                _statics = null;
                DeLog.Error("DE.S7 statics reporter start failed (everything else keeps running): " + e);
            }
            DeLog.Info("DE.S7 host start: conventions " + S7Conventions.Header + " " + DeLog.Hex(S7Conventions.Hash) +
                       " statics=" + (_statics != null ? "on" : "off") + " bones=de.bones landmarks=" + (S7Landmarks.Hidden ? "hidden" : "shown") +
                       " (" + S7Landmarks.EnvVar + ", de.s7 {landmarks}, " + S7Landmarks.Cmd + ")");
        }

        public S7Statics Statics { get { return _statics; } }

        /// <summary>Per frame (main thread), after the other hosts. Never throws.</summary>
        public void Tick(ulong nowMs)
        {
            if (_disposed) return;
            S7Landmarks.Update(nowMs); // never throws
            S7Statics s = _statics;
            if (s == null) return;
            try { s.Update(nowMs); }
            catch (Exception e)
            {
                DeLog.Error("DE.S7 statics DISABLED after an exception (everything else keeps running): " + e);
                DisposeStatics();
            }
        }

        /// <summary>C7: capabilities bit 2 while the statics reporter runs.</summary>
        public void FillStatus(ref SnStatus s)
        {
            S7Statics st = _statics;
            if (st != null) st.FillStatus(ref s);
        }

        /// <summary>C11: <c>de.statics</c>, <c>de.bones</c>.</summary>
        public bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            if (req == null) return false;
            if (S7Landmarks.Handle(req, reply, now)) return true; // S7-1: de.landmarks (de.s7 {landmarks} is hooked in S7aHost)
            S7Statics st = _statics;
            if (st != null && st.Handle(req, reply, now)) return true;
            if (st == null && req.Cmd == S7Statics.Cmd)
            {
                reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", "the statics reporter is off (start failed or disabled; see the log)");
                return true;
            }
            return SkinnedAstronaut.Handle(req, reply, now);
        }

        private void DisposeStatics()
        {
            S7Statics st = _statics;
            _statics = null;
            try { if (st != null) st.Dispose(); } catch (Exception) { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            DisposeStatics();
            try { S7Landmarks.RestoreAll("S7 host disposed"); } catch (Exception) { } // the bridge leaves Subnautica as it found it
        }
    }
}
