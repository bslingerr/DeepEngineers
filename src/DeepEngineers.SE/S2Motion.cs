using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using Sandbox.Game.World;
using VRage.Game.Utils;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The S2 dev command <c>de.motion</c>: drives SE's spectator along a
 /// <see cref="MotionPlan"/> one sample per plugin Update, before this tick's PrepareForDraw, exactly like S1's
 /// <c>de.path</c> (same spectator setter, update thread only). Reply <c>{ok, line, startTick, endTick}</c> (SE ticks,
 /// as the stand-in SE answers). Log lines: <c>DE.Motion start …</c>, <c>DE.Motion done …</c> / <c>aborted …</c>.
 /// </summary>
    internal static class S2Motion
    {
        private static MotionPlan _plan;
        /// <summary>
 /// The map the running plan's SN coordinates are in. Chart 0 never changes: the
 /// earlier sessions see no difference.
 /// </summary>
        private static SiteMap _planMap;
        private static int _k;
        private static Vec3 _fwdSn, _upSn;
        private static ulong _startTick;
        private static double _maxDy;

        public static bool Active { get { return _plan != null; } }

        /// <summary>DevChannel.ExtraCommand: the reply, or null when the command is not <c>de.motion</c>.</summary>
        public static Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            if (cmd != "de.motion") return null;
            try
            {
                SiteMap site;
                string why;
                if (!S1Commands.Ready(out site, out why)) return Fail("de.motion: " + why);
                if (S1Commands.PathActive) return Fail("de.motion: an S1 de.path is running");
                MyCamera cam = MySector.MainCamera;
                SeMatrix cur = FrameWriter.ToSe(cam.WorldMatrix);
                Vec3 p = cur.Translation;
                Vec3 e, u, n;
                site.FrameAt(p, out e, out u, out n);
                bool stop;
                MotionPlan plan = MotionPlan.Parse(args, site.SeToSn(p), out stop, out why);
                if (stop)
                {
                    string l = _plan != null ? "DE.Motion stopped at update " + _k + "/" + _plan.Ticks + " (de.motion stop via " + via + ")" : "DE.Motion stop: nothing running";
                    _plan = null;
                    DeLog.Info(l);
                    return Ok(l);
                }
                if (plan == null) return Fail(why);
                _fwdSn = site.DirSeToSn(e, u, n, cur.Forward);
                _upSn = site.DirSeToSn(e, u, n, cur.Up);
                _plan = plan;
                _planMap = site;
                _k = 0;
                _maxDy = 0;
                _startTick = FrameWriter.LastTick + 1;
                string line = "DE.Motion start " + plan.Label + ": " + plan.Describe() + " (heading kept; one sample per SE update from tick ~" +
                              _startTick + "; via " + via + ")";
                DeLog.Info(line);
                var r = Ok(line);
                r["startTick"] = (double)_startTick;
                r["endTick"] = (double)(_startTick + (ulong)plan.Ticks);
                return r;
            }
            catch (Exception ex) { return Fail("de.motion: " + ex.GetType().Name + ": " + ex.Message); }
        }

        /// <summary>Plugin Update (update thread, before PrepareForDraw): this tick's sample.</summary>
        public static void Tick(bool inWorld)
        {
            MotionPlan plan = _plan;
            if (plan == null) return;
            try
            {
                SiteMap site = _planMap ?? SiteBinding.Map;
                if (!inWorld || site == null || SiteBinding.Map == null) { Abort("left the world or the site is no longer bound"); return; }
                if (S1Commands.PathActive) { Abort("an S1 de.path started"); return; }
                Vec3 q = plan.PointAt(_k);
                SeMatrix m = S1Math.SpectatorAt(site, q, _fwdSn, _upSn);
                string why;
                if (!S1Commands.SetSpectator(m, out why)) { Abort(why); return; }
                double dy = Math.Abs(site.SeToSn(m.Translation).Y - q.Y);
                if (dy > _maxDy) _maxDy = dy;
                if (_k >= plan.Ticks)
                {
                    _plan = null;
                    DeLog.Info("DE.Motion done " + plan.Label + ": " + (plan.Ticks + 1) + " samples from tick ~" + _startTick +
                               "; SE self-check max|F(P).y - commanded y| = " + _maxDy.ToString("G6", System.Globalization.CultureInfo.InvariantCulture) + " m");
                    return;
                }
                _k++;
            }
            catch (Exception e) { Abort(e.GetType().Name + ": " + e.Message); }
        }

        private static void Abort(string why)
        {
            if (_plan == null) return;
            DeLog.Info("DE.Motion aborted " + _plan.Label + " at update " + _k + "/" + _plan.Ticks + ": " + why);
            _plan = null;
        }

        private static Dictionary<string, object> Ok(string line)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", true }, { "line", line } };
        }

        private static Dictionary<string, object> Fail(string error)
        {
            DeLog.Info("DE.Dev " + error);
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", false }, { "error", error }, { "line", error } };
        }
    }
}
