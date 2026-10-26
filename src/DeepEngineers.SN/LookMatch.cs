using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using uSky;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S7t T5 look match, Subnautica's game side (ROADMAP "### S7t Seamless transition" T5; rules in <see cref="SnLookRules"/>;
 /// CONTRACT §3.4, §1.1, §5.4 "S7t look match"). Above the surface, rising toward the band, the sun's shadow strength
 /// and the ambient move a little toward SE's harder light; Subnautica's sky, fog, water and colour grade stay its own
 /// (the reference palette), and nothing changes at or below the surface. Members VERIFIED
 /// Assembly-CSharp: <c>uSkyManager.main</c> (public static), <c>uSkyManager.SunLight</c> (public
 /// GameObject; <c>OnEnable</c> falls back to <c>GameObject.Find("Directional Light")</c>), <c>uSkyManager.Exposure</c>
 /// (public float; read every Update by <c>uSkyLight.SunAndMoonLightUpdate</c> and the sky material),
 /// <c>uSky.uSkyLight</c> (on the same GameObject, <c>[RequireComponent(typeof(uSkyManager))]</c>):
 /// <c>SunIntensity</c> and <c>ambientLight</c> (public floats, read every Update: the sun light's intensity and
 /// <c>_UweTopAmbientColor</c>/<c>_UweBottomAmbientColor</c> = sky/ground colour × indirect fraction × ambientLight);
 /// <c>UnityEngine.Light.shadowStrength</c> on the sun light (no game code writes it at run time; only the protobuf
 /// serializer of saved lights). <c>MainCamera.camera</c> (Assembly-CSharp-firstpass, public static) for the altitude.
 /// The time of day and the sun direction are <see cref="DayNightApply"/>'s (S7a): never touched here. Bridge hooks
 /// (S7t-look-match): <see cref="Frame"/> each frame, <see cref="Handle"/> for <c>de.s7t {look}</c>,
 /// <see cref="Restore"/> at quit.
 /// </summary>
    internal static class LookMatchSn
    {
        public static readonly SnLookTable Table = new SnLookTable();
        private static readonly SnLookSlot[] Slots = MakeSlots();
        private static readonly SnLookRowCadence Cadence = new SnLookRowCadence();
        private static readonly double[] Current = new double[SnLookRules.Keys.Length];
        private static readonly SnLookKey KShadow = SnLookRules.Find("shadow"), KAmbient = SnLookRules.Find("ambient"),
            KSun = SnLookRules.Find("sun"), KExposure = SnLookRules.Find("exposure");

        private static double _w, _wTarget, _y = double.NaN;
        private static string _why = "init";
        private static ulong _lastMs;
        private static bool _init;
        private static uSkyManager _sky;
        private static uSkyLight _light;
        private static Light _sun;
        public static long Frames, Writes, Rows, Restores;
        public static string DisabledWhy;

        private static SnLookSlot[] MakeSlots()
        {
            var s = new SnLookSlot[SnLookRules.Keys.Length];
            for (int i = 0; i < s.Length; i++) s[i] = new SnLookSlot();
            return s;
        }

        /// <summary>Bridge, every frame after the S7a tick (S7t-look-match hook). Never throws.</summary>
        public static void Frame(ulong now, bool inWorld, bool peerAlive)
        {
            if (DisabledWhy != null) return;
            try { FrameCore(now, inWorld, peerAlive); }
            catch (Exception e)
            {
                DisabledWhy = e.GetType().Name + ": " + e.Message;
                DeLog.Error("DE.S7t look DISABLED after " + DisabledWhy + " (Subnautica's own light from now on; the bridge keeps running)\n" + e);
                try { RestoreCore("disabled"); } catch (Exception) { }
            }
        }

        private static void Init()
        {
            _init = true;
            var applied = new List<string>();
            string env = null;
            try { env = Environment.GetEnvironmentVariable(SnLookRules.EnvVar); } catch (Exception) { }
            List<string> problems = Table.Apply(SnLookRules.Split(env), applied);
            DeLog.Info("DE.S7t look match (Subnautica): " + Table.Describe() + (applied.Count > 0 ? " (env " + SnLookRules.EnvVar + ": " + string.Join(" ", applied.ToArray()) + ")" : "") +
                       (problems.Count > 0 ? " env problems: " + string.Join("; ", problems.ToArray()) : ""));
        }

        private static void FrameCore(ulong now, bool inWorld, bool peerAlive)
        {
            if (!_init) Init();
            Frames++;
            double dt = _lastMs == 0 || now < _lastMs ? 0.0 : (now - _lastMs) / 1000.0;
            if (dt > 0.25) dt = 0.25;
            _lastMs = now;

            Camera cam = MainCamera.camera;
            _y = cam != null ? (double)cam.transform.position.y : double.NaN;
            var gi = new SnLookGateInputs
            {
                Enabled = Table.Enabled,
                InWorld = inWorld,
                PeerAlive = peerAlive,
                C5Fresh = S7aSnState.C5Valid && S7aSnState.C5Ms != 0 && now >= S7aSnState.C5Ms && now - S7aSnState.C5Ms <= SnLookRules.C5FreshMs,
                PictureSite = S7aSnState.PictureSite,
                Y = _y,
                LowY = Table.LowY,
                HighY = Table.HighY,
                Force = Table.Force,
            };
            string why;
            double target = SnLookRules.Target(ref gi, out why);
            double prev = _w;
            _wTarget = target;
            _w = SnLookRules.Ease(_w, target, dt, Table.EaseS);
            bool edge = why != _why || (prev > 0.0) != (_w > 0.0) || ((prev >= 1.0) != (_w >= 1.0));
            _why = why;

            if (!Resolve()) { if (Cadence.Due(now, false, edge)) Row(edge ? "edge" : "periodic"); return; }
            Read();
            for (int i = 0; i < SnLookRules.Keys.Length; i++)
            {
                SnLookKey k = SnLookRules.Keys[i];
                double v = Slots[i].Frame(k, Current[i], Table.Get(k), _w);
                if (!double.IsNaN(v)) Write(k, v);
            }
            bool active = false;
            foreach (SnLookSlot s in Slots) active |= s.Applied;
            if (Cadence.Due(now, active, edge)) { Read(); Row(edge ? "edge" : "periodic"); }
        }

        /// <summary>The sky objects; a new uSkyManager (scene change) forgets the captured values without writing.</summary>
        private static bool Resolve()
        {
            uSkyManager sky = uSkyManager.main;
            if (!ReferenceEquals(sky, _sky) || (sky != null && (_light == null || _sun == null)))
            {
                if (!ReferenceEquals(sky, _sky)) foreach (SnLookSlot s in Slots) s.Forget();
                _sky = sky;
                _light = sky != null ? sky.GetComponent<uSkyLight>() : null;
                _sun = sky != null && sky.SunLight != null ? sky.SunLight.GetComponent<Light>() : null;
            }
            return sky != null;
        }

        private static void Read()
        {
            Current[KShadow.Index] = _sun != null ? (double)_sun.shadowStrength : double.NaN;
            Current[KAmbient.Index] = _light != null ? (double)_light.ambientLight : double.NaN;
            Current[KSun.Index] = _light != null ? (double)_light.SunIntensity : double.NaN;
            Current[KExposure.Index] = _sky != null ? (double)_sky.Exposure : double.NaN;
        }

        private static void Write(SnLookKey k, double v)
        {
            if (double.IsNaN(v)) return;
            if (k == KShadow) { if (_sun != null) _sun.shadowStrength = (float)v; }
            else if (k == KAmbient) { if (_light != null) _light.ambientLight = (float)v; }
            else if (k == KSun) { if (_light != null) _light.SunIntensity = (float)v; }
            else if (k == KExposure) { if (_sky != null) _sky.Exposure = (float)v; }
            else return;
            Writes++;
        }

        /// <summary>Quit, SE's exit or a new SE generation (S7t-look-match hook): every captured value back at once.</summary>
        public static void Restore(string why)
        {
            try { RestoreCore(why); }
            catch (Exception e) { DeLog.Warn("DE.S7t look restore (" + why + "): " + e.GetType().Name + ": " + e.Message); }
        }

        private static void RestoreCore(string why)
        {
            bool any = false;
            foreach (SnLookSlot s in Slots) any |= s.Applied;
            _w = 0.0;
            if (!any) return;
            if (!ReferenceEquals(uSkyManager.main, _sky) || _sky == null) { foreach (SnLookSlot s in Slots) s.Forget(); return; }
            Read();
            for (int i = 0; i < SnLookRules.Keys.Length; i++)
            {
                double v = Slots[i].Release(Current[i]);
                if (!double.IsNaN(v)) Write(SnLookRules.Keys[i], v);
            }
            Restores++;
            DeLog.Info("DE.S7t look restored Subnautica's own light (" + why + ")");
            Row("restore");
        }

        // ---------------------------------------------------------------------------------------------- C11

 /// <summary><c>de.s7t {look: …}</c> (relayed from SE's <c>/de sn s7t look …</c>); false for the other S7t keys.</summary>
        public static bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            if (req == null || req.Cmd != SnLookRules.Cmd) return false;
            List<string> tokens;
            if (!SnLookRules.IsMine(req.Args, out tokens)) return false;
            if (!_init) Init();
            var applied = new List<string>();
            List<string> problems = Table.Apply(tokens, applied);
            var sb = new StringBuilder("DE.S7t look (Subnautica) ");
            if (applied.Count > 0) sb.Append("applied ").Append(string.Join(" ", applied.ToArray())).Append("; ");
            if (problems.Count > 0) sb.Append("problems: ").Append(string.Join("; ", problems.ToArray())).Append("; ");
            sb.Append("w=").Append(_w.ToString("0.00", CultureInfo.InvariantCulture)).Append(" (").Append(_why).Append(", y=")
              .Append(double.IsNaN(_y) ? "n/a" : _y.ToString("0", CultureInfo.InvariantCulture)).Append(" m) ").Append(Table.Describe());
            if (DisabledWhy != null) sb.Append(" DISABLED (").Append(DisabledWhy).Append(')');
            string line = sb.ToString();
            DeLog.Info(line);
            reply.Bool("ok", problems.Count == 0).Str("cmd", req.Cmd).Str("side", "sn").Str("line", line).Num("w", _w).Num("wTarget", _wTarget)
                 .Str("gate", _why).Str("table", Table.Describe()).Num("writes", Writes).Num("restores", Restores);
            if (problems.Count > 0) reply.Str("error", string.Join("; ", problems.ToArray()));
            Row("command");
            return true;
        }

        // ---------------------------------------------------------------------------------------------- rows

 /// <summary>One <c>s7t.look</c> row (CONTRACT §5.4 "S7t look match"): weight, gate and [own, now] per key.</summary>
        private static void Row(string why)
        {
            try
            {
                JsonOut j = S5Rows.Begin("s7t.look");
                j.Str("side", "sn").Str("why", why).Num("snFrame", (long)Time.frameCount).Str("gate", _why).Num("w", Math.Round(_w, 4)).Num("wTarget", Math.Round(_wTarget, 4))
                 .Num("y", Math.Round(_y, 1)).Num("low", Table.LowY).Num("high", Table.HighY).Num("ease", Table.EaseS)
                 .Num("force", Table.Force).Bool("enabled", Table.Enabled).Bool("sky", _sky != null)
                 .Bool("sunLight", _sun != null).Bool("uSkyLight", _light != null).Num("frames", Frames).Num("writes", Writes).Num("restores", Restores)
                 .Num("rowsDropped", Cadence.Dropped);
                if (DisabledWhy != null) j.Str("disabled", DisabledWhy);
                j.Obj("live");
                for (int i = 0; i < SnLookRules.Keys.Length; i++)
                {
                    SnLookKey k = SnLookRules.Keys[i];
                    SnLookSlot s = Slots[i];
                    j.Arr(k.Name).Num(null, s.Applied ? s.Own : Current[i]).Num(null, Current[i]).Num(null, Table.Get(k)).EndArr();
                }
                j.EndObj();
                long ext = 0;
                foreach (SnLookSlot s in Slots) ext += s.ExternalChanges;
                j.Num("externalChanges", ext);
                S5Rows.End(j);
                Rows++;
            }
            catch (Exception) { }
        }
    }
}
