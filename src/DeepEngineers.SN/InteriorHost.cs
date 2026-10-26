using System;
using DeepEngineers.Wire;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The interiors-and-lights host of the Subnautica side: created by the bridge after S1 and S3, ticked every frame after the other hosts.
 /// <list type="bullet">
 /// <item><see cref="S7Interior"/>: the inside-walkable rule (with <see cref="InteriorPatches"/>, installed here once),
 /// the interior/boundary chunk skies, the clip proxies, the rows and <c>de.interior</c>;</item>
 /// <item><see cref="LightMirror"/>: the C2 light rows as a pool of Unity lights, the rows and <c>de.lights</c>.</item>
 /// </list>
 /// Each feature is guarded: an exception disables that feature only (logged); the host never takes S0-S7a down.
 /// </summary>
    internal sealed class InteriorHost
    {
        private readonly S1Host _s1;
        private readonly MirrorConsumer _m;
        public readonly S7Interior Interior;
        public readonly LightMirror Lights;
        private bool _interiorOff, _lightsOff, _disposed;

        public InteriorHost(S1Host s1, MirrorConsumer mirror)
        {
            if (mirror == null) throw new ArgumentNullException("mirror");
            _s1 = s1;
            _m = mirror;
            Interior = new S7Interior(s1, mirror);
            Lights = new LightMirror(mirror);
        }

        public void Start()
        {
            InteriorPatches.Install();
            Lights.Start();
            DeLog.Info("DE.S7 interiors+lights start: conventions " + S7Conventions.Header + " " + DeLog.Hex(S7Conventions.Hash) + " (pinned " +
                       DeLog.Hex(S7Conventions.PinnedHash) + ")" + " patch=" + (S7Interior.PatchOn ? "on" : "off") + " mutants=" + DevMutants.Describe());
            if (S7Conventions.Hash != S7Conventions.PinnedHash) DeLog.Error("DE.S7 conventions hash DRIFT: compiled " + DeLog.Hex(S7Conventions.Hash));
        }

        public void Tick(ulong now)
        {
            if (_disposed) return;
            if (!_interiorOff)
            {
                try { Interior.Tick(now); }
                catch (Exception e) { _interiorOff = true; S7Interior.InsideNow = false; DeLog.Error("DE.S7 interior DISABLED after an exception (lights and S0-S7a keep running): " + e); }
            }
            if (!_lightsOff)
            {
                try { Lights.Tick(now); }
                catch (Exception e) { _lightsOff = true; DeLog.Error("DE.S7 lights DISABLED after an exception (interiors and S0-S7a keep running): " + e); try { Lights.Dispose(); } catch (Exception) { } }
            }
        }

        public bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            if (req.Cmd == "de.interior")
            {
                if (_interiorOff) { reply.Str("cmd", req.Cmd).Bool("ok", false).Str("error", "S7 interior disabled after an exception"); return true; }
                return Interior.Handle(req, reply, now);
            }
            if (req.Cmd == "de.lights")
            {
                if (_lightsOff) { reply.Str("cmd", req.Cmd).Bool("ok", false).Str("error", "S7 lights disabled after an exception"); return true; }
                return Lights.Handle(req, reply, now);
            }
            return false;
        }

        public void OnNewSePeer()
        {
            try { Interior.OnNewSePeer(); } catch (Exception e) { DeLog.Warn("DE.S7 interior new peer: " + e.GetType().Name + ": " + e.Message); }
            try { Lights.OnNewSePeer(); } catch (Exception e) { DeLog.Warn("DE.S7 lights new peer: " + e.GetType().Name + ": " + e.Message); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            S7Interior.InsideNow = false;
            try { Interior.Dispose(); } catch (Exception) { }
            try { Lights.Dispose(); } catch (Exception) { }
        }
    }
}
