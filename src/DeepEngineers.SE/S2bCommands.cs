using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;
using Sandbox.Engine.Utils;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRage.Game;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The S2b dev command <c>de.swim</c> (C11 and <c>/de swim</c>, dev builds; ROADMAP S2b "Work items"), handled in this
 /// order inside one call:
 /// <list type="bullet">
 /// <item><c>{model: on|off}</c>: the swim model (off = stock SE movement; on by default once the site is bound);</item>
 /// <item><c>{camera: first|third|keep}</c> alone: SE's view of the local character (S5-2c, unattended n7).</item>
 /// <item><c>{teleportChar: [x, y, z], yawDeg, camera: keep}</c>: the local character's origin (feet) to the SN
 /// point, upright on the planet, facing the SN heading (yaw 0 = north); its velocity is zeroed and, unless
 /// <c>camera</c> is <c>keep</c>, a spectator camera is handed back to the character (SE alone: the tests start at
 /// −50 m);</item>
 /// <item><c>{script: [...] | "compact" | "stop"}</c>: the scripted input source (<see cref="SwimScript"/>);</item>
 /// <item><c>{velocitySource: poststep|readback|model}</c> (dev): where the model's input velocity comes from while it
 /// keeps control (<see cref="SwimController.VelocitySource"/>; default poststep);</item>
 /// <item><c>{gain: auto|off|&lt;factor&gt;}</c> (dev): the in-step compensation (<see cref="SwimGain"/>, default auto);</item>
 /// <item><c>{rows: on|auto}</c>: <c>on</c> = a <c>swim</c> row every tick the model is evaluated (the launcher's
 /// scripted session); <c>auto</c> (default) = every tick while a script runs, else once a second in the water and at
 /// every transition;</item>
 /// <item><c>{fins: off|basic|ultra}</c> (S5a): the model's fins modifier (+0 / +1.9 / +3.2 m/s; default ultra);</item>
 /// <item><c>{o2: dump}</c>: the oxygen rule's numbers; <c>{o2: &lt;0..1&gt;}</c> / <c>{o2: "auto"}</c> (S5a, dev): the
 /// override of the O2 fraction mirrored to Subnautica through C1 (test only, logged; the suit is untouched);</item>
 /// <item><c>{dump}</c> (also the default): the controller's state (S5a adds <c>fins</c>, <c>o2Override</c>, <c>wire</c>).</item>
 /// </list>
 /// Reply <c>{ok, line, …}</c>; the dump fields are merged into it.
 /// </summary>
    internal static class S2bCommands
    {
        public static Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            if (cmd != "de.swim") return null;
            try
            {
                if (!SwimController.Enabled)
                    return Fail("de.swim: the swim controller is off (" + SwimController.EnvVar + "); nothing to do");
                var lines = new List<string>();
                var r = Ok("");
                bool acted = false;

                string model = MiniJson.GetString(args, "model");
                if (model != null)
                {
                    bool? on = MiniJson.GetBool(args, "model");
                    if (!on.HasValue) return Fail("de.swim: model must be on or off");
                    lines.Add(SwimController.SetModel(on.Value, via));
                    acted = true;
                }

                object tp;
                if (args != null && args.TryGetValue("teleportChar", out tp))
                {
                    string why;
                    string line = TeleportChar(tp, args, via, out why);
                    if (line == null) return Fail("de.swim teleportChar: " + why);
                    lines.Add(line);
                    acted = true;
                }

                object camObj;
                if (args != null && args.TryGetValue("camera", out camObj) && !args.ContainsKey("teleportChar"))
                {
                    string why;
                    string line = SetCamera(camObj as string, via, out why);
                    if (line == null) return Fail("de.swim camera: " + why);
                    lines.Add(line);
                    acted = true;
                }

                object sc;
                if (args != null && args.TryGetValue("script", out sc))
                {
                    bool stop;
                    string err;
                    SwimScript s = SwimScript.Parse(sc, out stop, out err);
                    if (s == null && !stop) return Fail("de.swim script: " + err);
                    lines.Add(SwimController.StartScript(s, via));
                    if (s != null)
                    {
                        r["steps"] = s.Steps.Length;
                        r["totalTicks"] = s.TotalTicks;
                    }
                    acted = true;
                }

                string vs = MiniJson.GetString(args, "velocitySource");
                if (vs != null)
                {
                    if (vs != SwimController.VelocityPostStep && vs != SwimController.VelocityReadback && vs != SwimController.VelocityModel)
                        return Fail("de.swim velocitySource must be poststep, readback or model");
                    SwimController.VelocitySource = vs;
                    lines.Add("DE.Swim velocity state from " + vs + " (via " + via + ")");
                    DeLog.Info(lines[lines.Count - 1]);
                    acted = true;
                }

                string rows = MiniJson.GetString(args, "rows");
                if (rows != null)
                {
                    if (rows != "on" && rows != "auto") return Fail("de.swim rows must be on or auto");
                    SwimController.RowsEveryTick = rows == "on";
                    lines.Add("DE.Swim rows " + (SwimController.RowsEveryTick ? "every tick" : "auto (scripts every tick, else 1/s in the water and transitions)") +
                              " (via " + via + ")");
                    DeLog.Info(lines[lines.Count - 1]);
                    acted = true;
                }

                object g;
                if (args != null && args.TryGetValue("gain", out g))
                {
                    string gerr;
                    if (!SwimController.Gain.SetMode(g, out gerr)) return Fail("de.swim: " + gerr);
                    lines.Add("DE.Swim compensation " + SwimController.Gain.Describe() + " (via " + via + ")");
                    DeLog.Info(lines[lines.Count - 1]);
                    acted = true;
                }

                string fins = MiniJson.GetString(args, "fins");
                if (fins != null)
                {
                    string line = SwimController.SetFins(fins, via);
                    if (line == null) return Fail("de.swim fins must be off, basic or ultra");
                    lines.Add(line);
                    acted = true;
                }

                object refillV;
                if (args != null && args.TryGetValue("refill", out refillV) &&
                    (refillV is bool ? (bool)refillV : string.Equals(refillV as string, "true", StringComparison.OrdinalIgnoreCase)))
                {
                    // S6-3: the scripted Survival session drowned the character (45 s tank, never surfacing)
                    string why;
                    string line = SwimController.RefillSuit(via, out why);
                    if (line == null) return Fail("de.swim refill: " + why);
                    lines.Add(line);
                    acted = true;
                }

                object o2v;
                if (args != null && args.TryGetValue("o2", out o2v))
                {
                    // S5a: a number 0..1 or "auto" = the mirrored-fraction override (dev, test only); "dump"/true = the rule's numbers
                    string o2s = o2v as string;
                    bool info = o2v is bool || o2s == null || o2s.Equals("dump", StringComparison.OrdinalIgnoreCase) || o2s.Equals("info", StringComparison.OrdinalIgnoreCase);
                    if (o2v is double || (o2s != null && !info))
                    {
                        string why;
                        string line = SwimController.SetO2Override(o2v, via, out why);
                        if (line == null) return Fail("de.swim o2: " + why);
                        lines.Add(line);
                        r["o2Override"] = double.IsNaN(SwimController.O2Override) ? null : (object)SwimController.O2Override;
                    }
                    else
                    {
                        r["o2"] = SwimController.O2Info();
                        lines.Add("DE.Swim o2 " + JsonText.Serialize(r["o2"]));
                    }
                    acted = true;
                }

                if (!acted || MiniJson.GetBool(args, "dump") == true)
                {
                    Dictionary<string, object> d = SwimController.Dump();
                    foreach (var kv in d) r[kv.Key] = kv.Value;
                    lines.Add("DE.Swim " + SwimController.StateName + " model=" + (SwimController.ModelOn ? "on" : "off") +
                              " hooks=" + (SwimController.HookVelocity ? "velocity" : "NO-velocity") + "," + (SwimController.HookInject ? "walk" : "NO-walk") +
                              " eyeY=" + Num(d["eyeY"]) + " script=" + (SwimController.ScriptRunning ? "running" : "none") +
                              " fins=" + SwimFins.Name(SwimController.Fins) + (double.IsNaN(SwimController.O2Override) ? "" : " o2Override=" + Num(SwimController.O2Override)) +
                              " model=" + SwimModelAdapter.ModelName + (S2bMutants.Active.Length > 0 ? " MUTANTS=" + S2bMutants.Active : "") +
                              (S5aMutants.Active.Length > 0 ? " MUTANTS=" + S5aMutants.Active : ""));
                }
                r["line"] = string.Join(" | ", lines.ToArray());
                return r;
            }
            catch (Exception e) { return Fail("de.swim: " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>The local character's origin to an SN point, upright, facing an SN heading; null with <paramref name="why"/>.</summary>
 /// <summary>
 /// S5-2c: <c>{camera: first|third|keep}</c> on its own (no teleport) switches SE's view of the local character
 /// (first = the Entity controller on the character, third = ThirdPersonSpectator), so an unattended n7 session
 /// has third-person rows for S5.N7a/N7b; <c>keep</c> does nothing.
 /// </summary>
        private static string SetCamera(string cam, string via, out string why)
        {
            why = null;
            string v = (cam ?? "").Trim().ToLowerInvariant();
            MySession s = MySession.Static;
            MyCharacter c = s != null ? s.LocalCharacter : null;
            if (c == null || c.Closed) { why = "no local character"; return null; }
            if (v == "keep") return "DE.Swim camera kept (via " + via + ")";
            if (v != "first" && v != "third") { why = "camera must be first, third or keep"; return null; }
            bool first = v == "first";
            c.IsInFirstPersonView = first;
            s.SetCameraController(first ? MyCameraControllerEnum.Entity : MyCameraControllerEnum.ThirdPersonSpectator, c, null);
            return "DE.Swim camera=" + v + " (via " + via + ")";
        }

        private static string TeleportChar(object value, IDictionary<string, object> args, string via, out string why)
        {
            why = null;
            SiteMap site;
            if (!S1Commands.Ready(out site, out why)) return null;
            double x, y, z;
            if (!Point(value, out x, out y, out z)) { why = "needs [x, y, z] (Subnautica metres) or \"x,y,z\""; return null; }
            double yaw = MiniJson.GetNumber(args, "yawDeg") ?? 0.0;
            string cam = MiniJson.GetString(args, "camera");
            return TeleportLocalCharacter(site, new Vec3(x, y, z), yaw, via, !string.Equals(cam, "keep", StringComparison.OrdinalIgnoreCase), out why);
        }

        /// <summary>
 /// The character teleport (de.swim teleportChar; the lifepod respawn rule): the character origin at the SN point
 /// with the heading, velocity 0, the swim latch re-evaluated next tick; a spectator camera is handed back to the
 /// character when <paramref name="handCamera"/>. Returns the log line, or null with <paramref name="why"/>.
 /// </summary>
        public static string TeleportLocalCharacter(SiteMap site, Vec3 q, double yaw, string via, bool handCamera, out string why)
        {
            why = null;
            MySession s = MySession.Static;
            MyCharacter c = s != null ? s.LocalCharacter : null;
            if (c == null || c.Closed) { why = "no local character"; return null; }
            if (c.IsDead) { why = "the character is dead"; return null; }
            if (c.IsSitting) { why = "the character is seated (leave the seat first)"; return null; }
            SeMatrix m = S1Math.SpectatorAt(site, q, S1Math.HeadingSn(yaw), Vec3.UnitY);
            MatrixD w = MatrixD.CreateWorld(new Vector3D(m.Translation.X, m.Translation.Y, m.Translation.Z),
                                            new Vector3D(m.Forward.X, m.Forward.Y, m.Forward.Z), new Vector3D(m.Up.X, m.Up.Y, m.Up.Z));
            // MyEntity.Teleport(MatrixD, object, bool), as SE's own player
 // teleport does
            c.Teleport(w, null, false);
            if (c.Physics != null) c.Physics.LinearVelocity = Vector3.Zero;
            SwimController.OnTeleported();
            bool handed = false;
            if (handCamera && ReferenceEquals(s.CameraController, MySpectatorCameraController.Static))
            {
                s.SetCameraController(MyCameraControllerEnum.Entity, c, null);
                handed = true;
            }
            Vec3 back = site.SeToSn(m.Translation);
            string line = "DE.Swim teleportChar sn=" + SiteBinding.V(q) + " yawDeg=" + Num(yaw) + " -> SE P=" + SiteBinding.V(m.Translation) +
                          " F(P)=" + SiteBinding.V(back) + " |F(P)-sn|=" + Num((back - q).Length) + " m (character origin, velocity 0" +
                          (handed ? ", camera back to the character" : "") + "; via " + via + ")";
            DeLog.Info(line);
            return line;
        }

        private static bool Point(object v, out double x, out double y, out double z)
        {
            x = y = z = 0;
            var list = v as IList<object>;
            if (list != null && list.Count == 3 && list[0] is double && list[1] is double && list[2] is double)
            {
                x = (double)list[0];
                y = (double)list[1];
                z = (double)list[2];
                return true;
            }
            string s = v as string;
            if (s == null) return false;
            string[] p = s.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            return p.Length == 3 &&
                   double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
                   double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y) &&
                   double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z);
        }

        private static string Num(object v)
        {
            if (v is double) return ((double)v).ToString("0.###", CultureInfo.InvariantCulture);
            return v == null ? "n/a" : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        private static string Num(double v) { return v.ToString("0.######", CultureInfo.InvariantCulture); }

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
