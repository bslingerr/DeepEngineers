using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The S7a command <c>de.s7</c> (C11 and <c>/de s7 …</c>; dev builds; CONTRACT §5.4 "S7a control commands"): the
 /// dispatcher. <c>{picture: "site" | "waterline"}</c> goes to <see cref="PicturePolicy"/>,
 /// <c>{ghost: "on" | "off" | "stop" | "probe", block}</c> to <see cref="PlacementGhost"/>,
 /// <c>{time: {sync: "on" | "off"} | &lt;c 0..1&gt; | "off"}</c> to <see cref="SunSync"/>; <c>{action: "status"}</c>
 /// (also the default) answers the picture policy, the ghost's status, the C1 locomotion extension and the sun, and
 /// writes one <c>s7a.sun</c> row (the launcher's n5 samples it every 2 s). Update thread.
 /// </summary>
    internal static class S7aCommands
    {
        public const string Cmd = "de.s7";

        public static Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            if (cmd != Cmd) return null;
            try
            {
                if (args == null) args = new Dictionary<string, object>(StringComparer.Ordinal);
                if (args.ContainsKey("band")) return ViewBand.Command(args);
                if (args.ContainsKey("picture")) return PicturePolicy.Command(args);
                if (args.ContainsKey("ghost")) return PlacementGhost.Command(args);
                if (args.ContainsKey("time")) return SunSync.Command(args);
                string action = "status";
                object a;
                if (args.TryGetValue("action", out a) && a is string) action = ((string)a).Trim().ToLowerInvariant();
                switch (action)
                {
                    case "status": return Status();
                    case "loco": return Loco();
                    case "sun":
                    {
                        Dictionary<string, object> d = SunSync.Status();
                        SunSync.Row("status");
                        d["ok"] = true;
                        d["line"] = SunSync.StatusLine();
                        return d;
                    }
                    default:
                        return Fail("de.s7: unknown action '" + action + "' (status | loco | sun; or {picture} | {ghost} | {time})");
                }
            }
            catch (Exception e)
            {
                DeLog.Info("DE.Error de.s7 " + e.GetType().Name + ": " + e.Message + " " + e.StackTrace);
                return Fail("de.s7 failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static Dictionary<string, object> Status()
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            uint policy = ViewPicture.Site;
            try { policy = PicturePolicy.Current; d["picture"] = ViewPicture.Name(policy); }
            catch (Exception e) { d["picture"] = "error " + e.GetType().Name; }
            try
            {
                // integrator: the latch as CONTRACT "S7a control commands" lists it (policy and latch, band tick,
 // picture blocked bits); the machine's last outputs, read on the update thread that steps it
                ViewStateDriver drv = S2Host.Driver;
                if (drv != null)
                {
                    ViewOutputs o = drv.Last;
                    d["policyEffective"] = ViewPicture.Name(o.PicturePolicy);
                    d["view"] = ViewStateAdapter.Name(drv.State);
                    d["latch"] = o.Picture;
                    d["bandTick"] = (long)drv.MachineState.BandTick;
                    d["pictureBlocked"] = (long)o.PictureBlocked;
                    d["pictureBlockedNames"] = ViewStateAdapter.BlockedNames(o.PictureBlocked);
                    d["pictureReason"] = o.PictureReason != ViewReason.None ? ViewReason.Name(o.PictureReason) : "none";
                    d["pictureEdges"] = drv.PictureEdges;
                }
                else d["latch"] = "no view machine (S2 off)";
            }
            catch (Exception e) { d["latch"] = "error " + e.GetType().Name; }
            d["band"] = ViewBand.Status();
            try { d["ghost"] = PlacementGhost.Status(); }
            catch (Exception e) { d["ghost"] = "error " + e.GetType().Name; }
            d["loco"] = LocoWireFill.Describe();
            d["locoWritten"] = LocoWireFill.Written;
            d["sun"] = SunSync.Status();
            d["conventionsHash"] = "0x" + S7aConventions.Hash.ToString("x16", CultureInfo.InvariantCulture);
            SunSync.Row("status");
            d["ok"] = true;
            d["line"] = "DE.S7a picture=" + ViewPicture.Name(policy) + (d.ContainsKey("view") ? " view=" + d["view"] + " latch=" + d["latch"] : "") + " loco: " + LocoWireFill.Describe() + "; " + SunSync.StatusLine();
            return d;
        }

        private static Dictionary<string, object> Loco()
        {
            LocoWireState s = LocoWireFill.Last;
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "ok", true }, { "tick", (long)LocoWireFill.LastTick }, { "state", S7aConventions.C1LocoState.Name(s.State) }, { "flags", (long)s.Flags },
                { "vF", (long)s.VForwardCm }, { "vS", (long)s.VStrafeCm }, { "vU", (long)s.VUpCm }, { "yawRate", (long)s.YawRateCdeg },
                { "raw", (long)s.SeMovement }, { "disabled", LocoWireFill.Disabled }, { "line", "DE.S7a loco " + LocoWireFill.Describe() },
            };
        }

        private static Dictionary<string, object> Fail(string error)
        {
            DeLog.Info("DE.S7a " + error);
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", false }, { "error", error }, { "line", error } };
        }
    }
}
