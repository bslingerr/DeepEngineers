using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DeepEngineers.Wire;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S3 "first block" on the Subnautica side (ROADMAP S3 SN plugin), driven by the bridge behaviour after S1 and S2:
 /// the C3/C4/C2 consumer and the mirrored entities (<see cref="MirrorConsumer"/>: worker-built mirrored meshes,
 /// MarmosetUBER from Addressables, SE textures, SkyApplier, Frame/Entity poses on the camera's bracket), the lab
 /// scene and its commands (<see cref="LabScene"/>: de.s3, de.time), the C7 counters and the S3 seeded failures
 /// (S3.M1-M8, DevMutants). Each feature runs in its own guard: its first exception is logged once
 /// (`DE.S3 FEATURE … DISABLED`) and only that feature stops; S0-S2 never depend on S3.
 /// </summary>
    internal sealed unsafe class S3Host : IDisposable
    {
        private readonly IS1Bridge _bridge;
        private readonly S1Host _s1;
        public readonly MirrorConsumer Mirror;
        public readonly LabScene Lab;
        private readonly HashSet<string> _disabled = new HashSet<string>(StringComparer.Ordinal);
        private S3MutantSet _mutants;
        private bool _started;

        // per-frame steps as cached delegates over fields (no closure allocated per frame, as S1Host does)
        private ulong _now;
        private long _frame;
        private bool _ready;
        private CamSample _sample;
        private bool _preCull;
        private readonly Action _stepWorld, _stepMirror, _stepLab, _stepPoses, _stepPreCull;

        public S3Host(MonoBehaviour host, IS1Bridge bridge, byte* sectionBase, ByteRing c3, ByteRing c4, S1Host s1)
        {
            _bridge = bridge;
            _s1 = s1;
            Mirror = new MirrorConsumer(sectionBase, c3, c4);
            Lab = s1 != null ? new LabScene(s1, Mirror, host, () => bridge.SnFrame) : null;
            _stepWorld = () => _ready = WorldReady();
            _stepMirror = () => Mirror.Tick(_now, _ready, _frame);
            _stepLab = () => Lab.Tick(_now, _ready);
            _stepPoses = () => Mirror.OnCameraSample(_sample, _preCull);
            _stepPreCull = () => Lab.OnPreCull();
        }

        public void Start()
        {
            _mutants = S3MutantSet.Current();
            Mirror.OnMutantsChanged(_mutants);
            Mirror.ApplyOptions(false, false);
            if (_s1 != null) _s1.CameraSampled = OnCameraSample;
            _started = true;
            DeLog.Info("DE.S3 start: mirror consumer (C2 poses, C3 models, C4 parts), lab commands de.s3/de.time" + (Lab == null ? " DISABLED (no S1 host)" : "") +
                       " options{" + Mirror.Options.Describe() + "} mutants=" + _mutants.Describe());
        }

        /// <summary>The host world is up: S1's ghost world set up, the Player and LargeWorld exist, no main menu.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool WorldReady()
        {
            if (_s1 == null || !_s1.InWorld) return false;
            return Player.main != null && LargeWorld.main != null && uGUI_MainMenu.main == null && MainCamera.camera != null;
        }

        public void Tick(ulong now)
        {
            if (!_started) return;
            S3MutantSet m = S3MutantSet.Current();
            if (!m.Equals(_mutants))
            {
                _mutants = m;
                DeLog.Warn("DE.S3 mutants now " + m.Describe());
                Guard("mirror", () => Mirror.OnMutantsChanged(m));
            }
            _now = now;
            _frame = (long)_bridge.SnFrame;
            _ready = false;
            Guard("world", _stepWorld);
            Guard("mirror", _stepMirror);
            if (Lab != null) Guard("lab", _stepLab);
        }

        /// <summary>
 /// Called by the S1 camera driver right after it placed the camera (LateUpdateLast, then the main camera's
 /// onPreCull): the mirrors follow the same SE ticks and alpha. Runs inside the camera driver's callbacks, so it
 /// must never throw (an exception there would disable the camera).
 /// </summary>
        private void OnCameraSample(CamSample s, bool preCull)
        {
            try
            {
                _sample = s;
                _preCull = preCull;
                Guard("poses", _stepPoses);
                if (preCull && Lab != null) Guard("lab", _stepPreCull);
            }
            catch (Exception)
            {
                // never let anything escape into the camera driver
            }
        }

        public bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            if (req.Cmd != "de.s3" && req.Cmd != "de.time") return false;
            if (Lab == null) { reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", "S3 lab unavailable (the S1 host did not start)"); return true; }
            if (Disabled("lab")) { reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", "the S3 lab feature is disabled after an exception (see DE.S3 FEATURE in the log)"); return true; }
            _ready = false;
            Guard("world", _stepWorld);
            bool ready = _ready;
            bool handled = true;
            Guard("lab", () => handled = Lab.Handle(req, reply, now, ready));
            reply.Str("featuresDisabled", _disabled.Count == 0 ? "none" : string.Join(",", new List<string>(_disabled).ToArray()));
            return handled;
        }

        /// <summary>C7 mirroredEntityCount, pendingParts, c3BytesDrained.</summary>
        public void FillStatus(ref SnStatus s)
        {
            Mirror.FillStatus(ref s);
        }

        public void OnNewSePeer() { Guard("mirror", Mirror.OnNewSePeer); }

        public void OnRingsResynced(string why) { Guard("mirror", () => Mirror.OnRingsResynced(why)); }

        public void OnHello(uint epoch) { Guard("mirror", () => Mirror.OnHello(epoch)); }

        public void Dispose()
        {
            try { if (_s1 != null) _s1.CameraSampled = null; } catch (Exception) { }
            try { if (Lab != null) Lab.Dispose(); } catch (Exception) { }
            try { Mirror.Dispose(); } catch (Exception) { }
            _started = false;
        }

        private bool Disabled(string feature) { return _disabled.Contains(feature); }

        /// <summary>Runs one feature step; its first exception disables that feature only (logged once).</summary>
        private void Guard(string feature, Action a)
        {
            if (_disabled.Contains(feature)) return;
            try
            {
                a();
            }
            catch (Exception e)
            {
                _disabled.Add(feature);
                DeLog.Error("DE.S3 FEATURE " + feature + " DISABLED after " + e.GetType().Name + ": " + e.Message + " (the rest of the bridge keeps running)\n" + e);
            }
        }
    }
}
