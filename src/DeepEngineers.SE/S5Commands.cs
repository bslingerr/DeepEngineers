using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The S5 command <c>de.s5</c> (C11 and <c>/de s5</c>; dev builds only; syntax <see cref="S5Args"/>): status, stats,
 /// set (enabled, character, enterM, leaveM, keepS, maxEntities, readBudgetMs), export &lt;entityId&gt; (s5.part rows of
 /// up to N random parts for S5.N1), character on|off, record {name, c1, c2} / record stop (the <c>.derec</c> stream
 /// recorder, CONTRACT §5.4 "Stream recording"), models {count, send} (the G1-E4 / N5 model set: the first N distinct
 /// cube block models, the census-listed RoundCornerTriangleRightPlate and InsetConnectorConstruction_1 first), coh
 /// {entityId, point, seconds} (s5.coh rows, S5.N4), trace seconds (s5.sub and s5.char bone rows every tick, S5.N3 /
 /// S5.N7a). Update thread.
 /// </summary>
    internal static class S5Commands
    {
        public static readonly string[] CensusModels = { "RoundCornerTriangleRightPlate", "InsetConnectorConstruction_1" };

        public static Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            if (cmd != "de.s5") return null;
            try
            {
                switch (S5Args.ActionOf(args))
                {
                    case "status": return Status();
                    case "stats": return Stats();
                    case "set": return Set(args);
                    case "export": return Export(args);
                    case "character": return Character(args);
                    case "record": return Record(args);
                    case "models": return Models(args);
                    case "coh": return Coh(args);
                    case "trace":
                    {
                        double s = MiniJson.GetNumber(args, "trace") ?? 10.0;
                        GridMirrorProducer.S5Trace(s);
                        return Ok("DE.S5 trace: s5.sub and s5.char bone rows every tick for " + s.ToString("0.###", CultureInfo.InvariantCulture) + " s (S5.N3, S5.N7a)");
                    }
                    default: return Status();
                }
            }
            catch (Exception e)
            {
                DeLog.Info("DE.Error de.s5 " + e.GetType().Name + ": " + e.Message + " " + e.StackTrace);
                return Fail("de.s5 failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static Dictionary<string, object> Status()
        {
            Dictionary<string, object> d = GridMirrorProducer.S5Status();
            d["ok"] = true;
            d["conventionsHash"] = "0x" + S5Conv.Hash().ToString("x16", CultureInfo.InvariantCulture);
            d["line"] = "DE.S5 " + (GridMirrorProducer.S5Active ? "on" : "off") + ": " + d["entities"] + " entities (" + d["grids"] + " grids, " + d["subgrids"] + " subgrids, " +
                        d["floating"] + " floating), " + d["parts"] + " parts, character " + CharacterExport.StateName + (GridMirrorProducer.Recorder.Active ? ", RECORDING " + GridMirrorProducer.Recorder.Name : "");
            return d;
        }

        private static Dictionary<string, object> Stats()
        {
            Dictionary<string, object> d = GridMirrorProducer.Status();
            d["s5"] = GridMirrorProducer.S5Status();
            d["ok"] = true;
            d["line"] = "DE.S5 stats (see the reply object; per second: se-*.jsonl s5.budget)";
            return d;
        }

        private static Dictionary<string, object> Set(IDictionary<string, object> args)
        {
            IDictionary<string, object> set = MiniJson.GetObject(args, "set");
            if (set == null)
            {
                set = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var kv in args) if (kv.Key != "set") set[kv.Key] = kv.Value;
            }
            string why;
            List<string> changed = GridMirrorProducer.S5Config.Apply(set, out why);
            if (changed == null) return Fail("de.s5 set: " + why);
            string line = "DE.S5 set " + string.Join(", ", changed.ToArray()) + " → " + JsonText.Serialize(GridMirrorProducer.S5Config.ToDict());
            DeLog.Info(line);
            return Ok(line);
        }

        private static Dictionary<string, object> Export(IDictionary<string, object> args)
        {
            object v;
            long id;
            if (!args.TryGetValue("entityId", out v) || !S5Args.TryEntityId(v, out id))
                if (!args.TryGetValue("export", out v) || !S5Args.TryEntityId(v, out id)) return Fail("de.s5 export: an entityId (number or decimal string)");
            int sample = (int)(MiniJson.GetNumber(args, "sample") ?? 20);
            int seed = (int)(MiniJson.GetNumber(args, "seed") ?? 1);
            int written;
            string why = GridMirrorProducer.ExportParts(id, sample, seed, out written);
            if (why != null) return Fail("de.s5 export: " + why);
            return Ok("DE.S5 export: " + written + " s5.part row(s) of entity " + id + " (sample " + sample + ", seed " + seed + "; S5.N1)");
        }

        private static Dictionary<string, object> Character(IDictionary<string, object> args)
        {
            object v;
            args.TryGetValue("character", out v);
            bool? on = S5Args.Bool(v);
            if (!on.HasValue) return Fail("de.s5 character: on or off");
            GridMirrorProducer.S5Config.Character = on.Value;
            string line = "DE.S5 character export " + (on.Value ? "ON" : "OFF (EntityRemove of the mirrored character)");
            DeLog.Info(line);
            return Ok(line);
        }

        private static Dictionary<string, object> Record(IDictionary<string, object> args)
        {
            object v;
            args.TryGetValue("record", out v);
            StreamRecorder rec = GridMirrorProducer.Recorder;
            var s = v as string;
            if (s != null && s.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase))
            {
                if (!rec.Active) return Fail("de.s5 record stop: not recording");
                string name = rec.Name;
                rec.Stop("de.s5 record stop");
                string line = "DE.S5 record " + name + " stopped: " + rec.FilePath + " (the sidecar .json follows when the writer thread is done)";
                DeLog.Info(line);
                return Ok(line);
            }
            if (!BuildFlavor.IsDev) return Fail("de.s5 record: dev builds only");
            var o = v as IDictionary<string, object>;
            string nm = o != null ? MiniJson.GetString(o, "name") : s;
            bool c1 = o != null && S5Args.Bool(o.ContainsKey("c1") ? o["c1"] : null) == true;
            bool c2 = o != null && S5Args.Bool(o.ContainsKey("c2") ? o["c2"] : null) == true;
            uint seVersion = (uint)VersionGate.Expected;
            string why;
            if (!rec.Start(StreamRecorder.DefaultDirectory, nm, c1, c2, seVersion, Phases.WorldEpoch, false, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), out why))
                return Fail("de.s5 record: " + why);
            bool restart = o == null || S5Args.Bool(o.ContainsKey("restart") ? o["restart"] : null) != false;
            string rs = restart ? GridMirrorProducer.RestartForRecording(nm) : "restart=false";
            string l = "DE.S5 recording " + nm + " (c1=" + c1 + ", c2=" + c2 + ") to " + rec.FilePath + "; " +
                       (rs == null ? "the stream restarts (ClearAll and everything) so the file holds every model and part" : "no stream restart: " + rs);
            DeLog.Info(l);
            return Ok(l);
        }

        private static Dictionary<string, object> Models(IDictionary<string, object> args)
        {
            IDictionary<string, object> o = MiniJson.GetObject(args, "models") ?? args;
            int count = (int)(MiniJson.GetNumber(o, "count") ?? 200);
            object sv;
            bool send = !o.TryGetValue("send", out sv) || S5Args.Bool(sv) != false;
            if (count < 1 || count > 5000) return Fail("de.s5 models: count 1..5000");
            int requested;
            string why = GridMirrorProducer.RequestModelSet(count, send, CensusModels, out requested);
            if (why != null) return Fail("de.s5 models: " + why);
            return Ok("DE.S5 models: " + requested + " distinct block model(s) requested" + (send ? ", sent over C3 once loaded" : "") + " (s5.model rows; S5.G1-E4, S5.N5)");
        }

        private static Dictionary<string, object> Coh(IDictionary<string, object> args)
        {
            IDictionary<string, object> o = MiniJson.GetObject(args, "coh") ?? args;
            object v;
            long id;
            if (!o.TryGetValue("entityId", out v) || !S5Args.TryEntityId(v, out id)) return Fail("de.s5 coh: an entityId");
            Vec3? point = null;
            object pv;
            if (o.TryGetValue("point", out pv) && pv is List<object>)
            {
                var l = (List<object>)pv;
                if (l.Count != 3) return Fail("de.s5 coh: point = [x, y, z] grid-local metres");
                double[] p = new double[3];
                for (int i = 0; i < 3; i++) { double? d = S5Args.Number(l[i]); if (!d.HasValue) return Fail("de.s5 coh: point must be numbers"); p[i] = d.Value; }
                point = new Vec3(p[0], p[1], p[2]);
            }
            double seconds = MiniJson.GetNumber(o, "seconds") ?? 10.0;
            string why = GridMirrorProducer.StartCoh(id, point, seconds);
            if (why != null) return Fail("de.s5 coh: " + why);
            return Ok("DE.S5 coh: s5.coh rows of entity " + id + " " + (point.HasValue ? "point " + point.Value : "(the seat, else the grid origin)") + " for " +
                      seconds.ToString("0.###", CultureInfo.InvariantCulture) + " s (S5.N4)");
        }

        private static Dictionary<string, object> Ok(string line)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", true }, { "line", line } };
        }

        private static Dictionary<string, object> Fail(string error)
        {
            DeLog.Info("DE.S5 " + error);
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", false }, { "error", error }, { "line", error } };
        }
    }
}
