using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;

namespace DeepEngineers.SE.Combat
{
    /// <summary>
 /// The S6 commands of the SE plugin (C11 and chat; dev builds only; syntax <see cref="S6Args"/>; CONTRACT §5.4 "S6
 /// control commands"):
 /// <list type="bullet">
 /// <item><c>de.s6 {status: true}</c>: the skin producer, the damage consumer, the world's gates, the character id, the
 /// mutants and the SE self-check result.</item>
 /// <item><c>de.s6 {skin: "&lt;entityId&gt;", rows: bool, resend: bool = true}</c>: the census of the skin SE sent (counts,
 /// per section its id, centre, box count, hash and up to 8 box centres — the launcher's damage plan); a resend is
 /// queued too (unless <c>resend: false</c>); <c>rows</c> writes an s6.skin census row with the section list.</item>
 /// <item><c>de.s6 {damage: {target, kind, section, local, amount, type, seq?, gen?, epoch?, root?, flags?, k?, unit?},
 /// apply: false|true}</c> or <c>{damage: {records: [ … ]}, apply}</c>: test records through the same checks as C8
 /// records (the SE-alone S6.N1 / S6.N10 harness when the fake SN host is not used); a dry run unless <c>apply</c>.</item>
 /// <item><c>de.s6 {selftest: true}</c>: <see cref="S6SelfTest"/> plus what the game shows (segmentation, gates).</item>
 /// <item><c>de.s6 {resend: "&lt;entityId&gt;" | "all"}</c>: rebuild and resend skins (version + 1).</item>
 /// <item><c>de.s6 {spawn: "&lt;TechType&gt; [n] [dist]" | {techType, count, dist}}</c>: relayed to Subnautica as
 /// <c>de.spawn {techType, count, dist}</c> over C11c (chat relay path: only while no harness holds C11; a harness talks
 /// to Subnautica directly).</item>
 /// </list>
 /// Update thread.
 /// </summary>
    internal static class S6Commands
    {
        public static Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            if (cmd != "de.s6") return null;
            try
            {
                switch (S6Args.ActionOf(args))
                {
                    case "skin": return Skin(args);
                    case "damage": return Damage(args, via);
                    case "selftest": return SelfTest();
                    case "resend": return Resend(args);
                    case "spawn": return Spawn(args);
                    default: return Status();
                }
            }
            catch (Exception e)
            {
                DeLog.Info("DE.Error de.s6 " + e.GetType().Name + ": " + e.Message + " " + e.StackTrace);
                return Fail("de.s6 failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static Dictionary<string, object> Status()
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            d["ok"] = true;
            d["enabled"] = S6Settings.Enabled;
            d["conventionsHash"] = "0x" + CombatConventions.Hash().ToString("x16", CultureInfo.InvariantCulture);
            d["damageScaleVersion"] = CombatConventions.DamageScale.Version;
            d["factors"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "kChar", CombatConventions.DamageScale.KChar }, { "kSmall", CombatConventions.DamageScale.KSmall }, { "kLarge", CombatConventions.DamageScale.KLarge },
            };
            d["skins"] = CollisionSkin.Status();
            Dictionary<string, object> dmg = DamageConsumer.Status();
            d["damage"] = dmg;
            d["characterId"] = CharacterExport.CurrentCharacterId.ToString(CultureInfo.InvariantCulture);
            d["mutants"] = S6Mutants.Active;
            d["selfTest"] = _selfTestLine;
            d["line"] = "DE.S6 " + (S6Settings.Enabled ? "on" : "OFF (" + S6Settings.EnvVar + ")") + ": skins sent " + CollisionSkin.SentCount + " (" +
                        ((Dictionary<string, object>)d["skins"])["sent"] + " grid(s) now), damage drained " + DamageConsumer.Drained + ", applied " + DamageConsumer.Applied +
                        ", rejected " + DamageConsumer.RejectedTotal() + (S6Mutants.Active.Length > 0 ? ", MUTANTS " + S6Mutants.Active : "");
            return d;
        }

        private static Dictionary<string, object> Skin(IDictionary<string, object> args)
        {
            object v;
            args.TryGetValue("skin", out v);
            long id;
            if (!S5Args.TryEntityId(v, out id)) return Fail("de.s6 skin: an entity id (decimal string)");
            bool rows = S5Args.Bool(args.ContainsKey("rows") ? args["rows"] : null) == true;
            bool resend = S5Args.Bool(args.ContainsKey("resend") ? args["resend"] : null) == true;   // review F13: a census reads, it does not resend by default
            Dictionary<string, object> d = CollisionSkin.CensusReply(id, rows);
            if (resend && CollisionSkin.IsTracked(id)) d["resendQueued"] = CollisionSkin.Resend(id) > 0;
            return d;
        }

        private static Dictionary<string, object> Resend(IDictionary<string, object> args)
        {
            object v;
            args.TryGetValue("resend", out v);
            var s = v as string;
            long id = 0;
            if (!(s != null && s.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)) && !S5Args.TryEntityId(v, out id))
                return Fail("de.s6 resend: an entity id or \"all\"");
            int n = CollisionSkin.Resend(id);
            string line = "DE.S6 resend: " + n + " skin(s) marked (" + (id == 0 ? "all" : id.ToString(CultureInfo.InvariantCulture)) + "; version + 1 once rebuilt)";
            DeLog.Info(line);
            return n > 0 || id == 0 ? Ok(line) : Fail("de.s6 resend: " + id + " is not a mirrored grid");
        }

        private static Dictionary<string, object> Damage(IDictionary<string, object> args, string via)
        {
            if (!BuildFlavor.IsDev) return Fail("de.s6 damage: dev builds only");
            IDictionary<string, object> d = MiniJson.GetObject(args, "damage");
            if (d == null) return Fail("de.s6 damage: {damage: {target, local, amount, …} | {records: [ … ]}, apply}");
            bool apply = S5Args.Bool(args.ContainsKey("apply") ? args["apply"] : null) == true;
            var specs = new List<IDictionary<string, object>>();
            object rv;
            if (d.TryGetValue("records", out rv))
            {
                var l = rv as List<object>;
                if (l == null) return Fail("de.s6 damage: records must be a list");
                foreach (object o in l)
                {
                    var od = o as IDictionary<string, object>;
                    if (od == null) return Fail("de.s6 damage: every record is an object");
                    specs.Add(od);
                }
            }
            else specs.Add(d);
            if (specs.Count > 1024) return Fail("de.s6 damage: at most 1,024 records per command");

            Link link = Plugin.Instance != null ? Plugin.Instance.LinkForS1 : null;
            uint gen, epoch;
            DamageConsumer.Defaults(link, out gen, out epoch);
            long characterId = CharacterExport.CurrentCharacterId;
            Func<long, SentSkin> skinOf = id => { SentSkin s; return CollisionSkin.TryGetSkin(id, out s) ? s : null; };
            var results = new List<object>();
            var outcomes = new Dictionary<string, object>(StringComparer.Ordinal);
            int applied = 0, bad = 0;
            double se = 0;
            for (int i = 0; i < specs.Count; i++)
            {
                DamageRecord r;
                string error;
                Dictionary<string, object> res;
                if (!S6Inject.TryBuild(specs[i], gen, epoch, DamageConsumer.NextInjectSeq, skinOf, characterId, out r, out error))
                {
                    bad++;
                    res = new Dictionary<string, object>(StringComparer.Ordinal) { { "index", i }, { "outcome", "bad spec" }, { "detail", error } };
                    Count(outcomes, "bad spec");
                }
                else
                {
                    bool ok = DamageConsumer.Inject(r, apply, out res, specs[i].ContainsKey("seq"));   // review F9
                    res["index"] = i;
                    object tag;
                    if (specs[i].TryGetValue("expect", out tag) && tag is string) res["expect"] = tag;
                    string key = ok ? (string)res["outcome"] : res.ContainsKey("reason") ? (string)res["reason"] : (string)res["outcome"];
                    Count(outcomes, key);
                    if (ok && apply) { applied++; object a; if (res.TryGetValue("applied", out a) && a is double) se += (double)a; }
                }
                if (results.Count < 200) results.Add(res);
            }
            Jsonl.Flush();
            string line = "DE.S6 damage via " + via + ": " + specs.Count + " record(s), " + (apply ? applied + " applied (" + S3Rows.G(Math.Round(se, 4)) + " SE units)" : "dry run") +
                          ", outcomes " + JsonText.Serialize(outcomes) + (bad > 0 ? ", " + bad + " bad spec(s)" : "");
            DeLog.Info(line);
            var reply = Ok(line);
            reply["apply"] = apply;
            reply["count"] = specs.Count;
            reply["applied"] = applied;
            reply["appliedSe"] = se;
            reply["outcomes"] = outcomes;
            reply["results"] = results;
            reply["generation"] = gen;
            reply["worldEpoch"] = epoch;
            reply["characterId"] = characterId.ToString(CultureInfo.InvariantCulture);
            return reply;
        }

        private static void Count(Dictionary<string, object> d, string key)
        {
            object v;
            d[key] = d.TryGetValue(key, out v) ? (object)((int)v + 1) : 1;
        }

        private static string _selfTestLine;

        private static Dictionary<string, object> SelfTest()
        {
            string detail;
            bool pass = S6SelfTest.Run(out detail);
            Dictionary<string, object> gate = DamageConsumer.Gate();
            Dictionary<string, object> skins = CollisionSkin.Status();
            string line = "DE.S6 selftest " + (pass ? "PASS" : "FAIL") + ": " + detail + "; segmentation " + skins["segmentation"] + "; gate " + JsonText.Serialize(gate);
            _selfTestLine = line;
            DeLog.Info(line);
            var d = pass ? Ok(line) : Fail(line);
            d["pass"] = pass;
            d["detail"] = detail;
            d["gate"] = gate;
            d["segmentation"] = skins["segmentation"];
            return d;
        }

        private static Dictionary<string, object> Spawn(IDictionary<string, object> args)
        {
            object v;
            args.TryGetValue("spawn", out v);
            Dictionary<string, object> sa;
            string usage = null;
            var o = v as IDictionary<string, object>;
            if (o != null)
            {
                var argv = new List<string> { "spawn", MiniJson.GetString(o, "techType") ?? "" };
                double? n = MiniJson.GetNumber(o, "count"), dist = MiniJson.GetNumber(o, "dist");
                if (n.HasValue || dist.HasValue) argv.Add((n ?? 1).ToString("R", CultureInfo.InvariantCulture));
                if (dist.HasValue) argv.Add(dist.Value.ToString("R", CultureInfo.InvariantCulture));
                sa = S6Args.ParseSpawn(argv.ToArray(), out usage);
            }
            else
            {
                var s = v as string;
                if (s == null) return Fail("de.s6 spawn: \"<TechType> [n] [dist]\" or {techType, count, dist}");
                var parts = new List<string> { "spawn" };
                parts.AddRange(s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
                sa = S6Args.ParseSpawn(parts.ToArray(), out usage);
            }
            if (sa == null) return Fail("de.s6 spawn: " + usage);
            var relay = S3Commands.RelayToSn;
            if (relay == null) return Fail("de.s6 spawn: no relay to Subnautica");
            string why = relay("de.spawn", sa, reply => DeLog.Info("DE.S6 de.spawn reply: " + (reply != null ? JsonText.Serialize(reply) : "none (timeout or link down)")));
            if (why != null) return Fail("de.s6 spawn not relayed: " + why + " (a harness attached to C11 sends de.spawn to Subnautica itself)");
            string line = "DE.S6 spawn relayed to Subnautica: de.spawn " + JsonText.Serialize(sa);
            DeLog.Info(line);
            return Ok(line);
        }

        private static Dictionary<string, object> Ok(string line)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", true }, { "line", line } };
        }

        private static Dictionary<string, object> Fail(string error)
        {
            DeLog.Info("DE.S6 " + error);
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", false }, { "error", error }, { "line", error } };
        }
    }
}
