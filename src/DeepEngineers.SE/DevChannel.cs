using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>
 /// Dev control (CONTRACT §5.1/§5.4 C11, ROADMAP §5.10), dev builds only, active only while the C0 `devControl`
 /// word is non-zero:
 /// <list type="bullet">
 /// <item>Harness → SE: while bit 0 is set, SE drains CtlToSe (C11a, ≤ 8 messages per tick) and answers on CtlFromSe
 /// (C11b); a reply that does not fit is dropped and counted (a plugin never waits).</item>
 /// SE runs its own commands and relays those meant for Subnautica on
 /// CtlToSn (C11c) after taking bit 1 (only while bit 0 is clear). Bit 1 is released after 60 s without relay
 /// traffic, so a harness can attach again. While SE holds bit 1 it also reads CtlFromSn (C11d) and shows the
 /// replies in chat (no harness is attached then, so the ring keeps a single consumer).</item>
 /// </list>
 /// Request `{"id": n, "cmd": "&lt;name&gt;", "args": {…}}`, reply `{"id": n, "ok": true|false, …}`.
 /// Game-agnostic: game facts come from the providers the plugin sets. Update thread only.
 /// <para>S9 release flavour: no harness (C11a/C11b never read); chat keeps `/de help`, `/de version` and
 /// the user tuning commands of <see cref="UserCommandRules"/>, and only those are relayed on C11c.</para>
 /// </summary>
    internal sealed class DevChannel
    {
        public const ulong RelayIdleReleaseMs = 60000;
        public const ulong PendingTimeoutMs = 30000;
        public const int MaxMessagesPerTick = 8;
        public const string ChatSender = "DE";

        private struct Pending
        {
            public string Cmd;
            public ulong SentMs;
            /// <summary>S3: called with the parsed reply (null on timeout) on the update thread; null for plain relays.</summary>
            public Action<IDictionary<string, object>> OnReply;
        }

        private readonly Link _link;
        private readonly byte[] _buf;
        private readonly Dictionary<long, Pending> _pending = new Dictionary<long, Pending>();
        private long _nextId = 1;
        private ulong _relayLastUseMs;
        private RateLimit _badLog = new RateLimit(10000);

        public long HarnessCommands { get; private set; }
        public long RelayedToSn { get; private set; }
        public long RepliesFromSn { get; private set; }
        public long Malformed { get; private set; }

        /// <summary>Fields of the `de.version` reply (plugin, wire, layout, site, game version, gate).</summary>
        public Func<Dictionary<string, object>> VersionInfo;
        /// <summary>Fields of the `de.state` reply (phases, link, epochs).</summary>
        public Func<Dictionary<string, object>> StateInfo;
        /// <summary>Runs the SE WM-1 subset again and returns its log line.</summary>
        public Func<string> SelfTestLine;
        /// <summary>S0.N12 hook: a game version to pretend, or null for the plain check; returns the DE.Version line.</summary>
        public Func<int?, string> VersionTest;
        /// <summary>Shows a line in SE's chat (sender, text).</summary>
        public Action<string, string> ShowChat;
        /// <summary>
 /// S1 test commands (de.teleport, de.path, de.look, de.pixel, de.site): (cmd, args, via) → reply, or null when the
 /// command is not one of them. Set by the plugin (S1Commands); keeps this class game-independent.
 /// </summary>
        public Func<string, IDictionary<string, object>, string, Dictionary<string, object>> ExtraCommand;

        public DevChannel(Link link)
        {
            _link = link;
            // the largest message a 256 KiB C11 ring accepts is half the ring
            _buf = new byte[(int)(Layout.Region(RegionId.C11aCtlToSe).Size / 2)];
        }

        // =============================================================================================== per tick

        public void Tick(ulong nowMs)
        {
            // S9-release-flavor: a release build never reads the harness ring C11a; it keeps only its own chat relay
 // (bit 1: the user tuning commands of UserCommandRules, their replies and the idle release below)
            if (_link.State != LinkState.Up) { DropPending(); return; }
            uint dc = _link.DevControlWord;
            if (dc == 0) return;
            if (UserCommandRules.HarnessReadAllowed(BuildFlavor.IsDev, dc)) DrainHarness();
            if (_link.RelayHeld)
            {
                DrainSnReplies(nowMs);
                ExpirePending(nowMs);
                if (_pending.Count == 0 && nowMs - _relayLastUseMs >= RelayIdleReleaseMs)
                    _link.ReleaseRelay("idle " + RelayIdleReleaseMs / 1000 + " s");
            }
        }

        private void DrainHarness()
        {
            for (int i = 0; i < MaxMessagesPerTick; i++)
            {
                uint type;
                int n;
                RingReadStatus st = _link.ReadToSe(_buf, out type, out n);
                if (st == RingReadStatus.Empty) return;
                if (st != RingReadStatus.Ok)
                {
                    Malformed++;
                    LogBad("CtlToSe read " + st + " (tail re-synced)");
                    return;
                }
                if (type != C11Type.Json) { Malformed++; LogBad("CtlToSe message type " + type + " ignored"); continue; }
                HarnessCommands++;
                string text = Decode(n);
                object idValue = null;
                Dictionary<string, object> reply;
                try
                {
                    var req = MiniJson.Parse(text) as IDictionary<string, object>;
                    if (req == null) throw new FormatException("not a JSON object");
                    req.TryGetValue("id", out idValue);
                    string cmd = MiniJson.GetString(req, "cmd") ?? "";
                    reply = Execute(cmd, MiniJson.GetObject(req, "args"), "harness");
                }
                catch (Exception e)
                {
                    Malformed++;
                    reply = Fail("malformed request: " + e.Message);
                }
                Send(idValue, reply);
            }
        }

        private void Send(object id, Dictionary<string, object> reply)
        {
            var full = new Dictionary<string, object>(StringComparer.Ordinal);
            full["id"] = id;
            foreach (var kv in reply) full[kv.Key] = kv.Value;
            byte[] bytes = Encoding.UTF8.GetBytes(JsonText.Serialize(full));
            RingWriteStatus st = _link.ReplyFromSe(bytes, bytes.Length);
            if (st != RingWriteStatus.Ok) LogBad("CtlFromSe reply dropped (" + st + "; dropped so far " + _link.DroppedReplies + ")");
        }

        private void DrainSnReplies(ulong nowMs)
        {
            for (int i = 0; i < MaxMessagesPerTick; i++)
            {
                uint type;
                int n;
                RingReadStatus st = _link.ReadFromSn(_buf, out type, out n);
                if (st == RingReadStatus.Empty) return;
                if (st != RingReadStatus.Ok) { Malformed++; LogBad("CtlFromSn read " + st + " (tail re-synced)"); return; }
                if (type != C11Type.Json) continue;
                RepliesFromSn++;
                string text = Decode(n);
                string label = "SN";
                IDictionary<string, object> parsed = null;
                Action<IDictionary<string, object>> callback = null;
                try
                {
                    var o = MiniJson.Parse(text) as IDictionary<string, object>;
                    parsed = o;
                    double? id = MiniJson.GetNumber(o, "id");
                    Pending p;
                    if (id.HasValue && _pending.TryGetValue((long)id.Value, out p))
                    {
                        _pending.Remove((long)id.Value);
                        label = "SN " + p.Cmd + " (" + (nowMs - p.SentMs) + " ms)";
                        callback = p.OnReply;
                    }
                }
                catch (Exception) { }
                DeLog.Info("DE.Dev reply from Subnautica: " + text);
                Show(label + ": " + Truncate(text, 400));
                if (callback != null) Callback(callback, parsed);
            }
        }

        private void ExpirePending(ulong nowMs)
        {
            if (_pending.Count == 0) return;
            List<long> old = null;
            foreach (var kv in _pending)
                if (nowMs - kv.Value.SentMs > PendingTimeoutMs) (old ?? (old = new List<long>())).Add(kv.Key);
            if (old == null) return;
            foreach (long id in old)
            {
                Pending p = _pending[id];
                Show("SN " + p.Cmd + " id=" + id + ": no reply after " + PendingTimeoutMs / 1000 + " s");
                _pending.Remove(id);
                if (p.OnReply != null) Callback(p.OnReply, null);
            }
        }

        /// <summary>The link went down: pending relays will never be answered (S3 reply handlers get null).</summary>
        private void DropPending()
        {
            if (_pending.Count == 0) return;
            var handlers = new List<Action<IDictionary<string, object>>>();
            foreach (var kv in _pending) if (kv.Value.OnReply != null) handlers.Add(kv.Value.OnReply);
            _pending.Clear();
            foreach (var h in handlers) Callback(h, null);
        }

        private static void Callback(Action<IDictionary<string, object>> cb, IDictionary<string, object> reply)
        {
            try { cb(reply); }
            catch (Exception e) { DeLog.Info("DE.Error relay reply handler " + e.GetType().Name + ": " + e.Message); }
        }

        // =============================================================================================== commands

        private Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            switch (cmd)
            {
                case "de.ping":
                {
                    var r = Ok();
                    r["side"] = "se";
                    r["pong"] = true;
                    r["seGeneration"] = _link.SeGeneration;
                    r["linkEpoch"] = _link.LinkEpoch;
                    return r;
                }
                case "de.version":
                    return Merge(Ok(), VersionInfo);
                case "de.state":
                    return Merge(Ok(), StateInfo);
                case "de.selftest":
                {
                    string line = SelfTestLine != null ? SelfTestLine() : "unavailable";
                    var r = line.StartsWith("DE.WireSelfTest PASS", StringComparison.Ordinal) ? Ok() : Fail("self-test failed");
                    r["line"] = line;
                    return r;
                }
                case "de.mutant":
                {
                    string name = MiniJson.GetString(args, "name");
                    bool? on = MiniJson.GetBool(args, "on");
                    if (name == null || !on.HasValue) return Fail("args {\"name\": \"S0.M8\", \"on\": true|false} required");
                    string result = Mutants.Set(name, on.Value);
                    DeLog.Info("DE.Mutant " + result + " (via " + via + ")");
                    var r = result.StartsWith("S0.", StringComparison.Ordinal) || result.StartsWith("S1.", StringComparison.Ordinal) ||
                            result.StartsWith("S2.", StringComparison.Ordinal) || result.StartsWith("S2b.", StringComparison.Ordinal) ||
                            result.StartsWith("S3.", StringComparison.Ordinal) || result.StartsWith("S4.", StringComparison.Ordinal) ||
                            result.StartsWith("S5a.", StringComparison.Ordinal) || result.StartsWith("S5.", StringComparison.Ordinal) ||
                            result.StartsWith("S6.", StringComparison.Ordinal) ? Ok() : Fail(result);
                    r["result"] = result;
                    return r;
                }
                case "de.versionTest":
                {
                    double? v = MiniJson.GetNumber(args, "gameVersion");
                    string line = VersionTest != null ? VersionTest(v.HasValue ? (int?)(int)v.Value : null) : "unavailable";
                    var r = Ok();
                    r["line"] = line;
                    r["refused"] = VersionGate.Refused;
                    return r;
                }
                default:
                {
                    var extra = ExtraCommand;
                    if (extra != null)
                    {
                        Dictionary<string, object> r = extra(cmd, args, via);
                        if (r != null) return r;
                    }
                    return Fail("unknown command '" + cmd + "' (SE: de.ping, de.version, de.state, de.selftest, de.mutant, de.versionTest, " +
                                "de.teleport, de.path, de.look, de.pixel, de.site, de.spawnplanet, de.g2e3, de.display, de.view, de.audio, de.zorder, de.swim, de.s3, de.terrain, de.s5, de.s6)");
                }
            }
        }

        // =================================================================================================== chat

 /// <summary>
 /// `/de …` typed in SE's chat (update thread). Returns true when the line was ours (it is then not sent as a
 /// chat message).
 /// </summary>
        public bool HandleChat(string text, ulong nowMs)
        {
            if (text == null) return false;
            string t = text.Trim();
            if (!(t.Equals("/de", StringComparison.OrdinalIgnoreCase) || t.StartsWith("/de ", StringComparison.OrdinalIgnoreCase)))
                return false;
            DeLog.Info("DE.Dev chat command: " + t);

            string[] argv = t.Substring(3).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string verb = argv.Length > 0 ? argv[0].ToLowerInvariant() : "help";
            // S9-release-flavor: a release build keeps /de help, /de version and the user tuning commands only
            if (!UserCommandRules.ChatAllowed(BuildFlavor.IsDev, argv)) { Show("/de " + verb + ": " + UserCommandRules.RefusalText); return true; }
            if (!BuildFlavor.IsDev && verb == "help") { foreach (string line in UserCommandRules.ReleaseHelp) Show(line); return true; }
            switch (verb)
            {
                case "help":
                    Show("/de ping | version | state | selftest | vtest [gameVersion] | mutant <id> on|off | sn <cmd> [args…]");
                    Show("/de site [repair|rebind] | teleport <x> <y> <z> [yawDeg] | path [lengthM samples altitude headingDeg] | look E|N|down|yaw+30|pitch+45 [repeat everyTicks] | pixel <x> <y> <z> | pixel ahead [m]");
                    Show(LabSpawn.ChatUsage + " | /de g2e3");
                    Show(S2Args.DisplayUsage + " | " + S2Args.ViewUsage + " | /de audio | /de zorder [reassert|state]");
                    Show(S2bArgs.SwimUsage);
                    Show(S3Args.ChatUsage);
                    Show(Terrain.S4Args.ChatUsage);
                    Show(S5Args.ChatUsage);
                    Show(Combat.S6Args.ChatUsage + " | " + Combat.S6Args.SpawnUsage);
                    Show(S7aUsage);
                    Show(LookMatchRules.Usage + " | /de sn s7t look shadow=0..1 ambient=f …"); // S7t-look-match
                    return true;
                case "display":
                case "view":
                case "audio":
                case "zorder":
                    RunS2FromChat(verb, argv);
                    return true;
                case "swim":
                    RunSwimFromChat(argv);
                    return true;
                case "s3":
                    RunS3FromChat(argv);
                    return true;
                case "s5":
                    RunS5FromChat(argv);
                    return true;
                case "s6":
                    RunS6FromChat(argv);
                    return true;
                case "spawn":
                {
                    // S6: relayed to Subnautica as de.spawn {techType, count, dist} (DevConsole "spawn <techType> <n> <dist>")
                    string usage;
                    Dictionary<string, object> sa = Combat.S6Args.ParseSpawn(argv, out usage);
                    if (sa == null) { Show("usage: " + usage); return true; }
                    Relay("de.spawn", sa, nowMs);
                    return true;
                }
                case "terrain":
                    RunTerrainFromChat(argv);
                    return true;
                case "site":
                case "spawnplanet":
                case "g2e3":
                case "teleport":
                case "path":
                case "look":
                case "pixel":
                case "water":
                case "s7":
                case "s7p": // S7p (shared verb): /de s7p above events|se|off | projections on|off | subparts on|off | scene | hold=… | gameplay
                case "s7t": // S7t-look-match (shared verb): /de s7t look key=value … (the words go as argv; each S7t handler answers its own first word)
                    RunExtraFromChat(verb, argv);
                    return true;
                case "s13": // S13 hook: /de s13 status | basis body|camera -> de.s13 {argv} (S13Se; dev builds)
                {
                    var w13 = new List<object>();
                    for (int i = 1; i < argv.Length; i++) w13.Add(argv[i]);
                    var x13 = ExtraCommand;
                    Dictionary<string, object> r13 = x13 != null ? x13("de.s13", new Dictionary<string, object>(StringComparer.Ordinal) { { "argv", w13 } }, "chat") : null;
                    object l13;
                    Show(r13 == null ? "/de s13: unknown" : r13.TryGetValue("line", out l13) && l13 is string ? (string)l13 : Truncate(JsonText.Serialize(r13), 400));
                    return true;
                }
                case "s16": // S16 hook: /de s16 status|ladder|damage on|off|watch <id>|remove <id> -> de.s16 {argv} (S16Commands; dev builds)
                {
                    var w16 = new List<object>();
                    for (int i = 1; i < argv.Length; i++) w16.Add(argv[i]);
                    var x16 = ExtraCommand;
                    Dictionary<string, object> r16 = x16 != null ? x16("de.s16", new Dictionary<string, object>(StringComparer.Ordinal) { { "argv", w16 } }, "chat") : null;
                    object l16;
                    Show(r16 == null ? "/de s16: unknown" : r16.TryGetValue("line", out l16) && l16 is string ? (string)l16 : Truncate(JsonText.Serialize(r16), 400));
                    return true;
                }
                case "s15": // S15 hook: /de s15 status|play <event>|voice|sky|water on|off|apply|orbit <km> -> de.s15 {argv} (S15Commands; dev builds)
                {
                    var w15 = new List<object>();
                    for (int i = 1; i < argv.Length; i++) w15.Add(argv[i]);
                    var x15 = ExtraCommand;
                    Dictionary<string, object> r15 = x15 != null ? x15("de.s15", new Dictionary<string, object>(StringComparer.Ordinal) { { "argv", w15 } }, "chat") : null;
                    object l15;
                    Show(r15 == null ? "/de s15: unknown" : r15.TryGetValue("line", out l15) && l15 is string ? (string)l15 : Truncate(JsonText.Serialize(r15), 400));
                    return true;
                }
                case "s12": // S12 hook: /de s12 status|air on|off|probe|breach <grid> x y z|repair <grid>|vent <id> <mode> [s] -> de.s12 {argv} (S12Commands; dev builds)
                {
                    var w12 = new List<object>();
                    for (int i = 1; i < argv.Length; i++) w12.Add(argv[i]);
                    var x12 = ExtraCommand;
                    Dictionary<string, object> r12 = x12 != null ? x12("de.s12", new Dictionary<string, object>(StringComparer.Ordinal) { { "argv", w12 } }, "chat") : null;
                    object l12;
                    Show(r12 == null ? "/de s12: unknown" : r12.TryGetValue("line", out l12) && l12 is string ? (string)l12 : Truncate(JsonText.Serialize(r12), 400));
                    return true;
                }
                case "ping":
                    Show("SE pong: link " + _link.State + ", peer " + _link.Peer + ", seGen " + _link.SeGeneration + ", linkEpoch " + _link.LinkEpoch);
                    Relay("de.ping", null, nowMs);
                    return true;
                case "version":
                    Show("SE " + JsonText.Serialize(VersionInfo != null ? VersionInfo() : null));
                    if (BuildFlavor.IsDev) Relay("de.version", null, nowMs); // S9-release-flavor: SE's own answer only
                    return true;
                case "state":
                    Show("SE " + JsonText.Serialize(StateInfo != null ? StateInfo() : null));
                    return true;
                case "selftest":
                    Show(SelfTestLine != null ? SelfTestLine() : "unavailable");
                    return true;
                case "vtest":
                {
                    int v;
                    int? arg = argv.Length > 1 && int.TryParse(argv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? (int?)v : null;
                    Show(VersionTest != null ? VersionTest(arg) : "unavailable");
                    return true;
                }
                case "mutant":
                    if (argv.Length < 3) { Show("usage: /de mutant <id> on|off"); return true; }
                    string res = Mutants.Set(argv[1], argv[2].Equals("on", StringComparison.OrdinalIgnoreCase));
                    DeLog.Info("DE.Mutant " + res + " (via chat)");
                    Show(res);
                    return true;
                case "sn":
                {
                    if (argv.Length < 2) { Show("usage: /de sn <cmd> [args…] (relayed to Subnautica as de.<cmd>)"); return true; }
                    string cmd = argv[1].StartsWith("de.", StringComparison.Ordinal) ? argv[1] : "de." + argv[1];
                    var list = new List<object>();
                    for (int i = 2; i < argv.Length; i++) list.Add(argv[i]);
                    var args = new Dictionary<string, object>(StringComparer.Ordinal) { { "argv", list } };
                    Relay(cmd, args, nowMs);
                    return true;
                }
                default:
                    Show("unknown /de command '" + verb + "' (try /de help)");
                    return true;
            }
        }

        /// <summary>The S1 chat verbs: argv → the C11 command's args, then <see cref="ExtraCommand"/>; the reply goes to chat.</summary>
        private const string WaterUsage = "/de water [key=value ...] | reset | textures | help  (keys: texture transparent lit foam waveHeight waveSpeed waveScale volumetric fog=r,g,b material colorIntensity specularity specularIntensity ambient distantRadiusOffset distantRadiusScaler distantSoftnessOffset distantSoftnessScaler distantSoftnessMultiplier)";

        /// <summary>S7a: <c>/de s7 …</c> → C11 <c>de.s7</c> (CONTRACT §5.4 "S7a control commands").</summary>
        private const string S7aUsage = "/de s7 [status|loco|sun] | band <enter> <exit>|default|status | picture site|waterline | ghost on|off|stop | ghost probe <TypeId/SubtypeId> | time sync on|off | time <c 0..1> | time off";

        private void RunExtraFromChat(string verb, string[] argv)
        {
            var extra = ExtraCommand;
            if (extra == null) { Show("/de " + verb + ": not available"); return; }
            var args = new Dictionary<string, object>(StringComparer.Ordinal);
            string usage = null;
            switch (verb)
            {
                case "site":
                    if (argv.Length > 1 && argv[1].Equals("repair", StringComparison.OrdinalIgnoreCase)) args["repair"] = true;
                    else if (argv.Length > 1 && argv[1].Equals("rebind", StringComparison.OrdinalIgnoreCase)) args["rebind"] = true;
                    else if (argv.Length > 1) usage = "/de site [repair|rebind]";
                    break;
                case "spawnplanet":
                    LabSpawn.ParseChat(argv, 1, args, out usage);
                    break;
                case "g2e3":
                    if (argv.Length > 1) usage = "/de g2e3 (no arguments; the rows go to the SE log as DE.G2E3 lines)";
                    break;
                case "teleport":
                    if (!Numbers(argv, 1, args, "x", "y", "z") ) usage = "/de teleport <x> <y> <z> [yawDeg] (Subnautica metres; yaw 0 = north)";
                    else if (argv.Length > 4 && !Numbers(argv, 4, args, "yawDeg")) usage = "/de teleport <x> <y> <z> [yawDeg]";
                    break;
                case "path":
                    if (argv.Length > 1 && !Numbers(argv, 1, args, "lengthM")) usage = "/de path [lengthM samples altitude headingDeg]";
                    else if (argv.Length > 2 && !Numbers(argv, 2, args, "samples")) usage = "/de path [lengthM samples altitude headingDeg]";
                    else if (argv.Length > 3 && !Numbers(argv, 3, args, "altitude")) usage = "/de path [lengthM samples altitude headingDeg]";
                    else if (argv.Length > 4 && !Numbers(argv, 4, args, "headingDeg")) usage = "/de path [lengthM samples altitude headingDeg]";
                    break;
                case "water":
                    // 3...)
                    for (int i = 1; i < argv.Length; i++)
                    {
                        string tok = argv[i];
                        if (tok.Equals("reset", StringComparison.OrdinalIgnoreCase)) { args["reset"] = true; continue; }
                        if (tok.Equals("textures", StringComparison.OrdinalIgnoreCase)) { args["textures"] = true; continue; }
                        if (tok.Equals("help", StringComparison.OrdinalIgnoreCase)) { usage = WaterUsage; break; }
                        int eq = tok.IndexOf('=');
                        if (eq <= 0) { usage = WaterUsage; break; }
                        args[tok.Substring(0, eq)] = tok.Substring(eq + 1);
                    }
                    break;
                case "s7":
                {
                    // /de s7 [status|loco|sun] | picture site|waterline | ghost on|off|stop | ghost probe <block> | time sync on|off | time <c>|off
                    string a1 = argv.Length > 1 ? argv[1].ToLowerInvariant() : "status";
                    if (a1 == "status" || a1 == "loco" || a1 == "sun") { args["action"] = a1; break; }
                    if (a1 == "picture" && argv.Length > 2) { args["picture"] = argv[2].ToLowerInvariant(); break; }
                    if (a1 == "band" && argv.Length > 3)
                    {
                        double be, bx;
                        if (double.TryParse(argv[2], NumberStyles.Float, CultureInfo.InvariantCulture, out be) &&
                            double.TryParse(argv[3], NumberStyles.Float, CultureInfo.InvariantCulture, out bx))
                        {
                            args["band"] = new List<object> { be, bx };
                            break;
                        }
                    }
                    if (a1 == "band") { args["band"] = argv.Length > 2 ? argv[2].ToLowerInvariant() : "status"; break; }
                    if (a1 == "ghost" && argv.Length > 3 && argv[2].Equals("probe", StringComparison.OrdinalIgnoreCase)) { args["ghost"] = "probe"; args["block"] = argv[3]; break; }
                    if (a1 == "ghost" && argv.Length > 2) { args["ghost"] = argv[2].ToLowerInvariant(); break; }
                    if (a1 == "time" && argv.Length > 3 && argv[2].Equals("sync", StringComparison.OrdinalIgnoreCase))
                    {
                        args["time"] = new Dictionary<string, object>(StringComparer.Ordinal) { { "sync", argv[3].ToLowerInvariant() } };
                        break;
                    }
                    if (a1 == "time" && argv.Length > 2)
                    {
                        double tc;
                        if (double.TryParse(argv[2], NumberStyles.Float, CultureInfo.InvariantCulture, out tc)) args["time"] = tc;
                        else args["time"] = argv[2].ToLowerInvariant();
                        break;
                    }
                    usage = S7aUsage;
                    break;
                }
                case "look":
                    if (argv.Length < 2) usage = "/de look E|N|down|yaw+30|pitch+45 [repeat] [everyTicks]";
                    else
                    {
                        args["dir"] = argv[1];
                        if (argv.Length > 2 && !Numbers(argv, 2, args, "repeat")) usage = "/de look <dir> [repeat] [everyTicks]";
                        else if (argv.Length > 3 && !Numbers(argv, 3, args, "everyTicks")) usage = "/de look <dir> [repeat] [everyTicks]";
                    }
                    break;
                case "pixel":
                    if (argv.Length > 1 && argv[1].Equals("ahead", StringComparison.OrdinalIgnoreCase))
                    {
                        args["ahead"] = 10.0;
                        if (argv.Length > 2 && !Numbers(argv, 2, args, "ahead")) usage = "/de pixel ahead [metres]";
                    }
                    else if (!Numbers(argv, 1, args, "x", "y", "z")) usage = "/de pixel <x> <y> <z> | /de pixel ahead [metres]";
                    break;
                case "s7p": // S7p-scene-above: k=v / "k v" / a lone action word, for every S7p feature (S7pScene.ChatArgs)
                {
                    var words = new List<string>();
                    for (int i = 1; i < argv.Length; i++) words.Add(argv[i]);
                    foreach (KeyValuePair<string, object> kv in S7pScene.ChatArgs(words)) args[kv.Key] = kv.Value;
                    break;
                }
                case "s7t": // S7t-look-match: the words as argv and as {firstWord: rest} (one number → double), so each S7t handler finds its own key
                {
                    var words = new List<object>();
                    for (int i = 1; i < argv.Length; i++) words.Add(argv[i]);
                    if (words.Count == 0) { usage = LookMatchRules.Usage; break; }
                    args["argv"] = words;
                    string rest = argv.Length > 2 ? string.Join(" ", argv, 2, argv.Length - 2) : "status";
                    double num;
                    if (argv.Length == 3 && double.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out num)) args[argv[1].ToLowerInvariant()] = num;
                    else args[argv[1].ToLowerInvariant()] = rest;
                    break;
                }
            }
            if (usage != null) { Show("usage: " + usage); return; }
            Dictionary<string, object> reply;
            try { reply = extra("de." + verb, args, "chat"); }
            catch (Exception e) { reply = Fail(e.GetType().Name + ": " + e.Message); }
            if (reply == null) { Show("/de " + verb + ": unknown"); return; }
            object line;
            if (reply.TryGetValue("line", out line) && line is string) Show((string)line);
            else Show(Truncate(JsonText.Serialize(reply), 400));
        }

        /// <summary>The S2 chat verbs (display, view, audio, zorder): S2Args → the C11 command's args → <see cref="ExtraCommand"/>.</summary>
        private void RunS2FromChat(string verb, string[] argv)
        {
            var extra = ExtraCommand;
            if (extra == null) { Show("/de " + verb + ": not available"); return; }
            string usage;
            Dictionary<string, object> args = S2Args.ParseChat(verb, argv, out usage);
            if (args == null) { Show("usage: " + usage); return; }
            Dictionary<string, object> reply;
            try { reply = extra("de." + verb, args, "chat"); }
            catch (Exception e) { reply = Fail(e.GetType().Name + ": " + e.Message); }
            if (reply == null) { Show("/de " + verb + ": unknown"); return; }
            object line;
            if (reply.TryGetValue("line", out line) && line is string) Show((string)line);
            Show(Truncate(JsonText.Serialize(reply), 400));
        }

        /// <summary>The S2b chat verb (<c>/de swim …</c>): S2bArgs → the C11 <c>de.swim</c> args → <see cref="ExtraCommand"/>.</summary>
        private void RunSwimFromChat(string[] argv)
        {
            var extra = ExtraCommand;
            if (extra == null) { Show("/de swim: not available"); return; }
            string usage;
            Dictionary<string, object> args = S2bArgs.ParseChat(argv, out usage);
            if (args == null) { Show("usage: " + usage); return; }
            Dictionary<string, object> reply;
            try { reply = extra("de.swim", args, "chat"); }
            catch (Exception e) { reply = Fail(e.GetType().Name + ": " + e.Message); }
            if (reply == null) { Show("/de swim: unknown"); return; }
            object line;
            if (reply.TryGetValue("line", out line) && line is string) Show(Truncate((string)line, 400));
            else Show(Truncate(JsonText.Serialize(reply), 400));
        }

        /// <summary>The S5 chat verb (<c>/de s5 …</c>): S5Args → the C11 <c>de.s5</c> args → <see cref="ExtraCommand"/>.</summary>
        private void RunS5FromChat(string[] argv)
        {
            var extra = ExtraCommand;
            if (extra == null) { Show("/de s5: not available"); return; }
            string usage;
            Dictionary<string, object> args = S5Args.ParseChat(argv, out usage);
            if (args == null) { Show("usage: " + usage); return; }
            Dictionary<string, object> reply;
            try { reply = extra("de.s5", args, "chat"); }
            catch (Exception e) { reply = Fail(e.GetType().Name + ": " + e.Message); }
            if (reply == null) { Show("/de s5: unknown"); return; }
            object line;
            if (reply.TryGetValue("line", out line) && line is string) Show(Truncate((string)line, 400));
            else Show(Truncate(JsonText.Serialize(reply), 400));
        }

        /// <summary>The S6 chat verb (<c>/de s6 …</c>): S6Args → the C11 <c>de.s6</c> args → <see cref="ExtraCommand"/>.</summary>
        private void RunS6FromChat(string[] argv)
        {
            var extra = ExtraCommand;
            if (extra == null) { Show("/de s6: not available"); return; }
            string usage;
            Dictionary<string, object> args = Combat.S6Args.ParseChat(argv, out usage);
            if (args == null) { Show("usage: " + usage); return; }
            Dictionary<string, object> reply;
            try { reply = extra("de.s6", args, "chat"); }
            catch (Exception e) { reply = Fail(e.GetType().Name + ": " + e.Message); }
            if (reply == null) { Show("/de s6: unknown"); return; }
            object line;
            if (reply.TryGetValue("line", out line) && line is string) Show(Truncate((string)line, 400));
            else Show(Truncate(JsonText.Serialize(reply), 400));
        }

        /// <summary>The S3 chat verb (<c>/de s3 …</c>): S3Args → the C11 <c>de.s3</c> args → <see cref="ExtraCommand"/>.</summary>
        private void RunS3FromChat(string[] argv)
        {
            var extra = ExtraCommand;
            if (extra == null) { Show("/de s3: not available"); return; }
            string usage;
            Dictionary<string, object> args = S3Args.ParseChat(argv, out usage);
            if (args == null) { Show("usage: " + usage); return; }
            Dictionary<string, object> reply;
            try { reply = extra("de.s3", args, "chat"); }
            catch (Exception e) { reply = Fail(e.GetType().Name + ": " + e.Message); }
            if (reply == null) { Show("/de s3: unknown"); return; }
            object line;
            if (reply.TryGetValue("line", out line) && line is string) Show(Truncate((string)line, 400));
            else Show(Truncate(JsonText.Serialize(reply), 400));
        }

        /// <summary>The S4 chat verb (<c>/de terrain …</c>): S4Args → the C11 <c>de.terrain</c> args → <see cref="ExtraCommand"/>.</summary>
        private void RunTerrainFromChat(string[] argv)
        {
            var extra = ExtraCommand;
            if (extra == null) { Show("/de terrain: not available"); return; }
            string usage;
            Dictionary<string, object> args = Terrain.S4Args.ParseChat(argv, out usage);
            if (args == null) { Show("usage: " + usage); return; }
            Dictionary<string, object> reply;
            try { reply = extra("de.terrain", args, "chat"); }
            catch (Exception e) { reply = Fail(e.GetType().Name + ": " + e.Message); }
            if (reply == null) { Show("/de terrain: unknown"); return; }
            object line;
            if (reply.TryGetValue("line", out line) && line is string) Show(Truncate((string)line, 400));
            else Show(Truncate(JsonText.Serialize(reply), 400));
        }

        /// <summary>Parses argv[first…] as invariant doubles into args[names…]; false when one is missing or not a number.</summary>
        private static bool Numbers(string[] argv, int first, Dictionary<string, object> args, params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                double v;
                if (first + i >= argv.Length ||
                    !double.TryParse(argv[first + i], NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    return false;
                args[names[i]] = v;
            }
            return true;
        }

        private void Relay(string cmd, Dictionary<string, object> args, ulong nowMs)
        {
            string why;
            RelayCore(cmd, args, nowMs, null, out why);
        }

        /// <summary>
 /// S3 (<c>de.s3 h=raycast</c>): relays a command to Subnautica over CtlToSn (taking devControl bit 1, so only
 /// while no harness is attached) and calls <paramref name="onReply"/> with the parsed reply, or with null after
 /// <see cref="PendingTimeoutMs"/>, on the update thread. False with <paramref name="why"/> when not relayed (a
 /// release build relays only the user tuning commands, e.g. the <c>de.s7p {fx}</c> copy; S9).
 /// </summary>
        internal bool RelayForReply(string cmd, Dictionary<string, object> args, ulong nowMs, Action<IDictionary<string, object>> onReply, out string why)
        {
            return RelayCore(cmd, args, nowMs, onReply, out why);
        }

        private bool RelayCore(string cmd, Dictionary<string, object> args, ulong nowMs, Action<IDictionary<string, object>> onReply, out string why)
        {
            why = null;
            // S9-release-flavor: a release build relays only the user tuning commands (UserCommandRules)
            if (!UserCommandRules.RequestAllowed(BuildFlavor.IsDev, cmd, args)) { why = "release build: " + UserCommandRules.RefusalText; Show(cmd + " not relayed: " + why); return false; }
            if (_link.State != LinkState.Up) { why = "Subnautica is not linked (renderer offline)"; Show(cmd + " not relayed: " + why); return false; }
            if (!_link.TryAcquireRelay(out why)) { Show(cmd + " not relayed: " + why); return false; }
            _relayLastUseMs = nowMs;
            long id = _nextId++;
            var req = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "id", id },
                { "cmd", cmd },
                { "args", args ?? new Dictionary<string, object>(StringComparer.Ordinal) },
            };
            string json = JsonText.Serialize(req);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            RingWriteStatus st = _link.SendToSn(bytes, bytes.Length);
            if (st == RingWriteStatus.Ok)
            {
                RelayedToSn++;
                _pending[id] = new Pending { Cmd = cmd, SentMs = nowMs, OnReply = onReply };
                DeLog.Info("DE.Dev relayed to Subnautica: " + json);
                Show("-> SN " + cmd + " id=" + id);
                return true;
            }
            DeLog.Info("DE.Dev relay dropped (" + st + "): " + json);
            Show("-> SN " + cmd + " dropped (" + st + ")");
            why = "CtlToSn " + st;
            return false;
        }

        // ================================================================================================ helpers

        private string Decode(int n)
        {
            try { return Encoding.UTF8.GetString(_buf, 0, n); }
            catch (Exception) { return ""; }
        }

        private void Show(string text)
        {
            Action<string, string> a = ShowChat;
            if (a != null) { try { a(ChatSender, text); } catch (Exception) { } }
        }

        private void LogBad(string text)
        {
            if (_badLog.Allow(Wire.Native.Win32.TickCount64())) DeLog.Info("DE.Dev " + text);
        }

        private static Dictionary<string, object> Ok()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", true } };
        }

        private static Dictionary<string, object> Fail(string error)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", false }, { "error", error } };
        }

        private static Dictionary<string, object> Merge(Dictionary<string, object> into, Func<Dictionary<string, object>> provider)
        {
            if (provider == null) return into;
            Dictionary<string, object> more = null;
            try { more = provider(); } catch (Exception e) { into["error"] = e.GetType().Name; }
            if (more != null) foreach (var kv in more) into[kv.Key] = kv.Value;
            return into;
        }

        private static string Truncate(string s, int max)
        {
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
