using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRage.Game.Utils;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S13 "Character and animation v2", the SE side (ROADMAP §14 "### S13"; CONTRACT §5.4 "S13 conventions"). Update
 /// thread only; every game call guarded (a failure turns off only the S13 part that failed, logged once, and never
 /// breaks C1, the swim model or the character export). Members VERIFIED
 /// <list type="bullet">
 /// <item><c>MyCharacter.GetHeadMatrix(bool includeY, bool includeX = true, bool forceHeadAnim = false, bool
 /// forceHeadBone = false, bool preferLocalOverSync = false)</c>: with includeY false the
 /// head's local yaw (free look) is left out, so the matrix is the body's yaw with the head's pitch
 /// (GetHeadMatrixInternal: pitch × yaw × CreateFromDir(WorldMatrix.Forward, WorldMatrix.Up));</item>
 /// <item><c>MyCharacter.IsInFirstPersonView</c> (public bool property); <c>MyCharacter.Physics.LinearVelocity</c>;
 /// <c>MySector.MainCamera.WorldMatrix</c>;</item>
 /// <item><c>MyEntity.Render.ColorMaskHsv</c> (public <c>Vector3</c> property of MyRenderComponentBase).</item>
 /// </list>
 /// Point 3: the held tool's colour (<see cref="ToolColour"/>, CharacterExport hook). Point 5: the third-person swim basis
 /// (<see cref="SwimBasis"/>, SwimController hook) and the C1 S13 extension (<see cref="FillWire"/>, FrameWriter hook):
 /// the character's velocity in its look frame, Subnautica's animation source. Rows (se-*.jsonl): <c>s13.swim</c> (1 Hz in
 /// the water and on a basis change: basis, firstPerson, the angle between SE's camera and the body's look, the look-frame
 /// velocity), <c>s13.tool</c> (each held tool sent: the entity's colour and the colour sent). Command <c>de.s13</c> (dev).
 /// </summary>
    internal static class S13Se
    {
        public const string Verb = "de.s13";

        /// <summary><c>de.s13 basis body|camera</c>: third person swims along the body (default) or along the camera (S2b's rule).</summary>
        public static bool BodyBasisOn = !string.Equals(Env("DEEPENGINEERS_S13_BASIS"), "camera", StringComparison.OrdinalIgnoreCase);

        private static readonly HashSet<string> Off = new HashSet<string>(StringComparer.Ordinal);
        private static S13Basis _basis, _rowBasis;
        private static ulong _basisTick;
        private static double _basisAngle = double.NaN;
        private static ulong _lastRowTick;
        private static S13CharWireState _last;
        public static long Filled, Rows, ToolsColoured, BodyTicks, CameraTicks, Errors;

        private static string Env(string name)
        {
            try { return Environment.GetEnvironmentVariable(name); } catch (Exception) { return null; }
        }

        private static void Disable(string what, Exception e)
        {
            Errors++;
            if (Off.Add(what)) DeLog.Info("DE.S13 " + what + " OFF after " + e.GetType().Name + ": " + e.Message + " (the S2b/S5 behaviour stays)");
        }

        // ============================================================================================= point 5: swim basis

 /// <summary>
 /// SwimController hook (Look, S13 point 5): the swim basis of this tick when SE's camera follows the character. True with
 /// <paramref name="look"/> = the body's look (body yaw, head pitch) in third person; false = keep SE's camera (first
 /// person, <c>de.s13 basis camera</c>, the mutant S13.M5, or a failure).
 /// </summary>
        public static bool SwimBasis(MyCharacter c, ulong tick, out MatrixD look)
        {
            look = MatrixD.Identity;
            if (c == null || Off.Contains("basis")) { Note(S13Basis.Camera, tick, double.NaN); return false; }
            try { return SwimBasisCore(c, tick, out look); }
            catch (Exception e) { Disable("basis", e); Note(S13Basis.Camera, tick, double.NaN); return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool SwimBasisCore(MyCharacter c, ulong tick, out MatrixD look)
        {
            look = MatrixD.Identity;
            bool first = c.IsInFirstPersonView;
            S13Basis b = S13SeRules.Choose(true, first, BodyBasisOn, S13SeMutants.CameraBasis);
            MatrixD body = c.GetHeadMatrix(false, true, false, true, true);
            double angle = double.NaN;
            MyCamera cam = MySector.MainCamera;
            if (cam != null)
            {
                Vector3D cf = cam.WorldMatrix.Forward, bf = body.Forward;
                angle = S13SeRules.AngleDeg(cf.X, cf.Y, cf.Z, bf.X, bf.Y, bf.Z);
            }
            Note(b, tick, angle);
            if (b != S13Basis.Body) return false;
            look = body;
            return true;
        }

        private static void Note(S13Basis b, ulong tick, double angle)
        {
            _basis = b;
            _basisTick = tick;
            _basisAngle = angle;
            if (b == S13Basis.Body) BodyTicks++;
            else if (b == S13Basis.Camera) CameraTicks++;
        }

        // ============================================================================================= the C1 S13 extension

 /// <summary>
 /// FrameWriter hook: writes the C1 S13 extension (0xC0-0xC7) for this tick: the controlled character's velocity in its
 /// look frame (<c>GetHeadMatrix(false, true, …)</c>), flags Valid, BodyBasis (the swim model used that frame this tick),
 /// Swimming (the C1 swim extension says the model set the velocity). No character: version 1, flags 0. A failure writes
 /// version 0 from then on (Subnautica keeps S5a's input rule).
 /// </summary>
        public static void FillWire(ref SeFrame f, MyCharacter c)
        {
            if (Off.Contains("wire")) { S13CharWire.Erase(ref f); return; }
            try { FillWireCore(ref f, c); }
            catch (Exception e) { Disable("wire", e); S13CharWire.Erase(ref f); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FillWireCore(ref SeFrame f, MyCharacter c)
        {
            var s = new S13CharWireState { Version = S13CharWire.Version };
            SwimWireState sw;
            bool swimOk = SwimWire.TryRead(ref f, out sw);
            bool controls = swimOk && sw.Has(SwimWireFlags.Controls);
            bool inWater = swimOk && sw.Has(SwimWireFlags.InWater);
            if (c != null && !c.Closed && c.Physics != null)
            {
                MatrixD look = c.GetHeadMatrix(false, true, false, true, true);
                Vector3 v = c.Physics.LinearVelocity;
                byte flags = S13Conventions.C1Flags.Valid;
                if (controls) flags |= S13Conventions.C1Flags.Swimming;
                if (controls && _basis == S13Basis.Body && _basisTick + 2 >= f.tick) flags |= S13Conventions.C1Flags.BodyBasis;
                Vector3D r = look.Right, u = look.Up, fw = look.Forward;
                s = S13CharWire.Compose(flags, v.X, v.Y, v.Z, r.X, r.Y, r.Z, u.X, u.Y, u.Z, fw.X, fw.Y, fw.Z);
            }
            S13CharWire.Write(ref f, ref s);
            _last = s;
            Filled++;
            if (inWater && (f.tick >= _lastRowTick + S13SeRules.RowEveryTicks || _rowBasis != _basis)) Row(f.tick, c, ref s, ref sw);
        }

        private static void Row(ulong tick, MyCharacter c, ref S13CharWireState s, ref SwimWireState sw)
        {
            _lastRowTick = tick;
            _rowBasis = _basis;
            Rows++;
            var sb = new StringBuilder(384);
            sb.Append("{\"t\":"); JsonText.AppendString(sb, Utc());
            sb.Append(",\"ev\":\"s13.swim\",\"tick\":").Append(tick.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"basis\":"); JsonText.AppendString(sb, S13SeRules.Name(_basis));
            sb.Append(",\"basisTick\":").Append(_basisTick.ToString(CultureInfo.InvariantCulture));
            bool first = false;
            try { first = c != null && !c.Closed && c.IsInFirstPersonView; } catch (Exception) { }
            sb.Append(",\"firstPerson\":").Append(first ? "true" : "false");
            sb.Append(",\"cameraBodyDeg\":").Append(double.IsNaN(_basisAngle) ? "null" : _basisAngle.ToString("0.###", CultureInfo.InvariantCulture));
            sb.Append(",\"bodyOn\":").Append(BodyBasisOn ? "true" : "false");
            sb.Append(",\"mutant\":").Append(S13SeMutants.CameraBasis ? "true" : "false");
            sb.Append(",\"flags\":").Append(s.Flags.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"vRight\":").Append(s.RightMs.ToString("0.##", CultureInfo.InvariantCulture));
            sb.Append(",\"vUp\":").Append(s.UpMs.ToString("0.##", CultureInfo.InvariantCulture));
            sb.Append(",\"vForward\":").Append(s.ForwardMs.ToString("0.##", CultureInfo.InvariantCulture));
            sb.Append(",\"moveForward\":").Append(sw.MoveForward.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"moveStrafe\":").Append(sw.MoveStrafe.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"mode\":").Append(sw.Mode.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"bodyTicks\":").Append(BodyTicks.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"cameraTicks\":").Append(CameraTicks.ToString(CultureInfo.InvariantCulture));
            sb.Append('}');
            Jsonl.WriteLine(sb.ToString());
        }

        private static string Utc() { return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); }

        // ============================================================================================= point 3: tool colour

 /// <summary>
 /// CharacterExport hook (TickTool, S13 point 3): the colour the held tool's PartAdd carries = the tool entity's own
 /// <c>Render.ColorMaskHsv</c> (SE's OldGray for hand tools), never (0, 0, 0); logs an <c>s13.tool</c> row.
 /// </summary>
        public static Vector3 ToolColour(MyEntity tool, string asset, string skin)
        {
            float h = S13SeRules.OldGrayH, s = S13SeRules.OldGrayS, v = S13SeRules.OldGrayV;
            if (!Off.Contains("tool"))
            {
                try
                {
                    Vector3 hsv = ToolHsv(tool);
                    h = hsv.X; s = hsv.Y; v = hsv.Z;
                }
                catch (Exception e) { Disable("tool", e); }
            }
            float oh, os, ov;
            S13SeRules.ToolColour(h, s, v, S13SeMutants.ToolColourZero, out oh, out os, out ov);
            ToolsColoured++;
            try
            {
                var sb = new StringBuilder(256);
                sb.Append("{\"t\":"); JsonText.AppendString(sb, Utc());
                sb.Append(",\"ev\":\"s13.tool\",\"asset\":"); JsonText.AppendString(sb, asset ?? "");
                sb.Append(",\"skin\":"); JsonText.AppendString(sb, skin ?? "");
                sb.Append(",\"entityHsv\":[").Append(F(h)).Append(',').Append(F(s)).Append(',').Append(F(v)).Append(']');
                sb.Append(",\"sentHsv\":[").Append(F(oh)).Append(',').Append(F(os)).Append(',').Append(F(ov)).Append(']');
                sb.Append(",\"mutant\":").Append(S13SeMutants.ToolColourZero ? "true" : "false").Append('}');
                Jsonl.WriteLine(sb.ToString());
            }
            catch (Exception) { }
            return new Vector3(oh, os, ov);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Vector3 ToolHsv(MyEntity tool)
        {
            if (tool == null || tool.Render == null) return new Vector3(S13SeRules.OldGrayH, S13SeRules.OldGrayS, S13SeRules.OldGrayV);
            return tool.Render.ColorMaskHsv;
        }

        private static string F(float x) { return x.ToString("0.####", CultureInfo.InvariantCulture); }

        // ============================================================================================= de.s13

 /// <summary><c>de.s13 {status} | {basis: body|camera}</c> (dev; chat <c>/de s13 status|basis body|camera</c>).</summary>
        public static Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            if (cmd != Verb) return null;
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                if (!BuildFlavor.IsDev) { d["ok"] = false; d["error"] = UserCommandRules.RefusalText; return d; }
                object v;
                string w0 = null, w1 = null;
                if (args != null && args.TryGetValue("argv", out v) && v is IList)
                {
                    var l = (IList)v;
                    if (l.Count > 0) w0 = Convert.ToString(l[0], CultureInfo.InvariantCulture).ToLowerInvariant();
                    if (l.Count > 1) w1 = Convert.ToString(l[1], CultureInfo.InvariantCulture).ToLowerInvariant();
                }
                if (args != null && args.TryGetValue("basis", out v)) { w0 = "basis"; w1 = Convert.ToString(v, CultureInfo.InvariantCulture).ToLowerInvariant(); }
                if (w0 == "basis")
                {
                    if (w1 != "body" && w1 != "camera") { d["ok"] = false; d["error"] = "basis: body|camera"; return d; }
                    BodyBasisOn = w1 == "body";
                    DeLog.Info("DE.S13 swim basis in third person = " + w1 + " (" + via + ")");
                }
                d["ok"] = true;
                d["basisThirdPerson"] = BodyBasisOn ? "body" : "camera";
                d["lastBasis"] = S13SeRules.Name(_basis);
                d["cameraBodyDeg"] = double.IsNaN(_basisAngle) ? null : (object)_basisAngle;
                d["filled"] = Filled;
                d["toolsColoured"] = ToolsColoured;
                d["off"] = string.Join(",", new List<string>(Off).ToArray());
                d["line"] = "DE.S13 basis(3rd)=" + (BodyBasisOn ? "body" : "camera") + " last=" + S13SeRules.Name(_basis) + " cameraBody=" +
                            (double.IsNaN(_basisAngle) ? "-" : _basisAngle.ToString("0.#", CultureInfo.InvariantCulture) + "°") + " c1=" + Filled +
                            " look=(" + _last.RightMs.ToString("0.##", CultureInfo.InvariantCulture) + "," + _last.UpMs.ToString("0.##", CultureInfo.InvariantCulture) +
                            "," + _last.ForwardMs.ToString("0.##", CultureInfo.InvariantCulture) + ") tools=" + ToolsColoured + (S13SeMutants.Active.Length > 0 ? " mutants=" + S13SeMutants.Active : "");
                return d;
            }
            catch (Exception e)
            {
                d["ok"] = false;
                d["error"] = e.GetType().Name + ": " + e.Message;
                return d;
            }
        }
    }
}
