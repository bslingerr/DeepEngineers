using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S7p-gameplay, the Subnautica host (ROADMAP "### S7p Polish pass" P11, P13, P14): owns the creature damage
 /// (<see cref="S7pCreatureDamage"/>), writes the camera's per-axis row (<c>s7p.cam</c>, P13) and answers
 /// <c>de.s7p</c> for its own keys (others' keys fall through to the next handler):
 /// <list type="bullet">
 /// <item><c>{damage: scale | "off" | "on"}</c>: the creature damage scale (env <c>DEEPENGINEERS_S7P_DAMAGE</c>, default 1);</item>
 /// <item><c>{mirrorMs: x}</c>: the S5 mirror's per-frame budgets scaled so the child budget is x ms (default 2; 0.25..20);</item>
 /// <item><c>{action: "gameplay"}</c>: the status of the three.</item>
 /// </list>
 /// Started by the bridge after S3 (its reader sits on S3's C2 reader), disposed with S3. Never throws into the bridge's
 /// tick (an exception disables only this host, logged once by the bridge).
 /// </summary>
    internal sealed class S7pGameplay : IDisposable
    {
        private readonly S7pCreatureDamage _damage;
        private readonly Func<CameraDriver> _cam;
        private ulong _nextCamRowMs;
        private long _lastNonFinite;
        private bool _disposed;

        public S7pGameplay(MirrorConsumer mirror, Func<CameraDriver> cam)
        {
            _damage = new S7pCreatureDamage(mirror);
            _cam = cam;
            string env = Environment.GetEnvironmentVariable(S7pDamageRules.EnvVar);
            string error;
            _damage.Scale = S7pDamageRules.ScaleFromEnv(env, out error);
            _damage.ScaleSource = string.IsNullOrEmpty(env) ? "default" : S7pDamageRules.EnvVar + "=" + env + (error != null ? " (" + error + ": default)" : "");
        }

        public void Start()
        {
            _damage.Start();
        }

        public void OnNewSePeer()
        {
            _damage.OnNewSePeer();
        }

        public void Tick(ulong nowMs)
        {
            if (_disposed) return;
            _damage.Tick(nowMs);
            if (nowMs >= _nextCamRowMs)
            {
                _nextCamRowMs = nowMs + 1000;
                CamRow();
            }
        }

        /// <summary>
 /// s7p.cam (P13; 1 Hz while the camera is driven): the presented camera's motion per axis over the last second
 /// (yaw about world up, pitch, roll; degrees, absolute increments), the frames sampled and the C1 samples refused as
 /// non-finite. SE's s7p.camera row carries the input per axis; an axis with input and ≈ 0° here and there is stuck.
 /// </summary>
        private void CamRow()
        {
            CameraDriver cam = _cam != null ? _cam() : null;
            if (cam == null || !cam.Installed) return;
            double y, p, r;
            int n;
            cam.Axis.Take(out y, out p, out r, out n);
            long nf = cam.NonFiniteSamples;
            if (n == 0 && nf == _lastNonFinite) return;
            JsonOut j = S5Rows.Begin("s7p.cam");
            j.Num("frames", (long)n).Num("yawDeg", y).Num("pitchDeg", p).Num("rollDeg", r).Num("nonFinite", nf - _lastNonFinite).Num("nonFiniteTotal", nf);
            S5Rows.End(j);
            _lastNonFinite = nf;
        }

        public bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            if (req == null || req.Cmd != S7pDamageRules.Cmd) return false;
            Dictionary<string, object> a = S3Args.ToDict(req.Args);
            object v, act;
            a.TryGetValue("action", out act);
            bool mine = a.ContainsKey("damage") || a.ContainsKey("mirrorMs") || (act as string) == "gameplay";
            if (!mine) return false; // another S7p feature's key: the next handler answers
            reply.Str("cmd", req.Cmd);
            if (a.TryGetValue("damage", out v))
            {
                double s;
                string error;
                if (!S7pDamageRules.TryParseScale(v, out s, out error)) { reply.Bool("ok", false).Str("error", error); return true; }
                _damage.Scale = s;
                _damage.ScaleSource = "de.s7p";
                DeLog.Info("DE.S7p damage scale " + s.ToString("0.###", CultureInfo.InvariantCulture) + (s <= 0 ? " (OFF)" : "") + " (de.s7p)");
            }
            if (a.TryGetValue("mirrorMs", out v))
            {
                double ms;
                if (!S1Args.TryDouble(v, out ms) || ms < 0.25 || ms > 20) { reply.Bool("ok", false).Str("error", "mirrorMs: 0.25..20 (the child budget; the others scale with it)"); return true; }
                S7pMirrorBudget.Scale = ms / S7pMirrorBudget.ChildCreateMs;
                DeLog.Info("DE.S7p mirror budgets x" + S7pMirrorBudget.Scale.ToString("0.###", CultureInfo.InvariantCulture) + " (children " + S7pMirrorBudget.ChildMs.ToString("0.##", CultureInfo.InvariantCulture) +
                           " ms, posts " + S7pMirrorBudget.PostBudgetMs.ToString("0.##", CultureInfo.InvariantCulture) + " ms, results " + S7pMirrorBudget.ResultsBudgetMs.ToString("0.##", CultureInfo.InvariantCulture) + " ms)");
            }
            reply.Bool("ok", true);
            _damage.Status(reply);
            reply.Num("mirrorBudgetScale", S7pMirrorBudget.Scale).Num("mirrorChildMs", S7pMirrorBudget.ChildMs).Num("mirrorPostMs", S7pMirrorBudget.PostBudgetMs)
                 .Num("mirrorResultsMs", S7pMirrorBudget.ResultsBudgetMs).Str("roomIndex", S7pRoomIndex.Describe());
            CameraDriver cam = _cam != null ? _cam() : null;
            if (cam != null) reply.Num("camNonFinite", cam.NonFiniteSamples);
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _damage.Dispose();
        }
    }
}
