using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.Utils;
using VRage.Input;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S17 (docs/S17-BRIEF.md; CONTRACT §5.4 "S17 conventions"): Subnautica's small fish feed the SE character. Game thread,
 /// once per tick from Plugin (S17 hook). SE has no entity for a fish: the target is the nearest Catchable row of the C9
 /// table Subnautica writes (format aim, <see cref="S17Aim"/>); F (SE's own USE control) sends one request in the C5 block
 /// (<see cref="S17SeWire"/>), Subnautica removes the fish and answers in the C7 block, and on a Caught answer the raw item
 /// of the kind goes into the character's inventory or at its feet (format item.give). Every game member VERIFIED in the
 /// decomp: <c>IMyInput.IsNewGameControlPressed(MyStringId)</c> with <c>MyControlsSpace.USE</c>
 /// <c>MyCharacter.EquippedTool</c>, <c>IsDead</c>,
 /// <c>GetHeadMatrix(bool, bool, bool, bool, bool)</c>; <c>MyScreenManager.GetScreenWithFocus is
 /// MyGuiScreenGamePlay</c>; <c>MyInventory.CanItemsBeAdded(MyFixedPoint,
 /// MyDefinitionId)</c> and <c>AddItems(MyFixedPoint, MyObjectBuilder_Base)</c>;
 /// <c>MyFloatingObjects.Spawn(MyPhysicalInventoryItem, Vector3D, Vector3D, Vector3D, MyPhysicsComponentBase, Action)</c>
 /// <c>MyPhysicalInventoryItem(MyFixedPoint, MyObjectBuilder_PhysicalObject, float)</c>
 /// <c>MyDefinitionManager.TryGetPhysicalItemDefinition(MyDefinitionId, out …)</c>
 /// <c>MyObjectBuilderSerializerKeen.CreateNewObject&lt;T&gt;(string)</c> of
 /// <c>MyObjectBuilder_ConsumableItem</c>. Single-player: the item is added by this client's plugin.
 /// </summary>
    internal static unsafe class S17Se
    {
        public const string Verb = "de.s17";
        private const int C9Bytes = SnCreaturesLimits.RecordsOffset + S17Conventions.C9.RowsMax * SnCreaturesLimits.RecordSize;

        private static readonly S17CatchBook Book = new S17CatchBook(((uint)Environment.TickCount & 0x7FFFFFFFu) | 1u);
        private static readonly byte[] _c9 = new byte[C9Bytes];
        private static readonly byte[] _c9Scratch = new byte[C9Bytes];
        private static uint _c9Seq;
        private static ulong _c9ChangedMs, _c9ReadMs, _nextC9Ms;
        private static int _c9Count;
        private static bool _primed, _inWorldLast;
        private static string _lastError;
        private static ulong _nextErrorMs;

        // the current target
        private static bool _hasTarget;
        private static SnCreature _target;
        private static string _targetKind;
        private static uint _hintFor;
        private static ulong _nextHintMs;
        private static IMyHudNotification _hint;

        // status every 5 s and a
 // DE.S17 line every 30 s say which condition holds the target back and where the nearest kind-row fish is
        private static ulong _nextStatusMs, _nextStatusLogMs;
        private static string _whyNot = "no tick yet", _nearKind = "";
        private static double _nearReach = -1, _nearPerp = -1;

        public static long Requests, Caught, Given, Dropped, Refused, Timeouts, Late, LateGiven, NoItem, Errors, C9Reads, C9Stale, UseObjectSkips;
        public static string Mode = "idle";

        /// <summary>Plugin hook (game thread, every tick). Never throws.</summary>
        public static void Tick(ulong nowMs, bool inWorld)
        {
            try
            {
                if (!S17Switch.Enabled) { Mode = "off (" + S17Switch.EnvVar + "=off)"; S17SeWire.Off(); return; }
                if (!inWorld)
                {
                    if (_inWorldLast) { Book.Reset(); _primed = false; _hasTarget = false; HideHint(); }
                    _inWorldLast = false;
                    Mode = "no world";
                    S17SeWire.Off();
                    return;
                }
                _inWorldLast = true;
                Plugin pl = Plugin.Instance;
                Link l = pl != null ? pl.LinkForS1 : null;
                bool up = l != null && l.State == LinkState.Up;
                if (up && l.HasSnStatus)
                {
                    SnStatus st = l.LastSnStatus;
                    S17Ack a;
                    if (S17Wire.TryReadAck(ref st, out a))
                    {
                        if (!_primed) { Book.Prime(ref a); _primed = true; }
                        else OnAck(ref a, nowMs);
                    }
                    else _primed = true;
                }
                if (Book.Pending && !Book.Published && S17SeWire.PublishedSeq == Book.Current.ReqSeq) Book.MarkPublished(Book.Current.ReqSeq, nowMs);   //  the ack clock starts at the C5 write
                if (Book.TimedOut(nowMs))
                {
                    Timeouts++;
                    Row("timeout", Book.Current, null, S17Conventions.Refusal.Name(S17Conventions.Refusal.Timeout), nowMs - Book.SentMs, Book.Published ? null : "unpublished");
                    DeLog.Info("DE.S17 no answer to catch request " + Book.Current.ReqSeq + (Book.Published ? " within " + S17Conventions.Catch.AckTimeoutMs + " ms of its C5 write" :
                               " (never written into C5 within " + S17Conventions.Catch.UnpublishedMs + " ms)") + ": no item for now (a later Caught answer still gives it)");
                }
                if (up && nowMs >= _nextC9Ms) { _nextC9Ms = nowMs + 50; ReadC9(l, nowMs); }
                FindTarget(up, nowMs);
                if (_hasTarget && UsePressed()) Request(nowMs);
                S17SeWire.Set(Book.Current);
                Mode = !up ? "link down" : _hasTarget ? "target " + _targetKind : Book.Pending ? "waiting" : "idle";
                if (nowMs >= _nextStatusMs) { _nextStatusMs = nowMs + 5000; StatusRow(nowMs, up); }
                if (nowMs >= _nextStatusLogMs) { _nextStatusLogMs = nowMs + 30000; DeLog.Info("DE.S17 status: " + Status() + "; held by: " + _whyNot); }
            }
            catch (Exception e)
            {
                Errors++;
                _lastError = e.GetType().Name + ": " + e.Message;
                if (nowMs >= _nextErrorMs) { _nextErrorMs = nowMs + 10000; DeLog.Info("DE.Error S17 " + _lastError + " " + e.StackTrace); }
            }
        }

        // ================================================================================================ C9 and the target

        private static void ReadC9(Link l, ulong nowMs)
        {
            uint seq;
            int count;
            if (!l.TryReadC9(_c9Scratch, _c9, out seq, out count)) return;   //  _c9 changes only on a coherent read
            C9Reads++;
            if (seq != _c9Seq) { _c9Seq = seq; _c9ChangedMs = nowMs; _c9ReadMs = nowMs; }
            _c9Count = count;
        }

        private static void FindTarget(bool up, ulong nowMs)
        {
            _hasTarget = false;
            _nearReach = -1; _nearPerp = -1; _nearKind = "";
            if (!up) { _whyNot = "link down"; HideHint(); return; }
            if (_c9ChangedMs == 0) { _whyNot = "no C9 read yet"; HideHint(); return; }
            if (nowMs - _c9ChangedMs > (ulong)S17Conventions.C9.StaleMs) { C9Stale++; _whyNot = "C9 stale " + (nowMs - _c9ChangedMs) + " ms (seq " + _c9Seq + ", rows " + _c9Count + ")"; HideHint(); return; }
            MySession s = MySession.Static;
            MyCharacter ch = s != null ? s.LocalCharacter : null;
            SiteMap map = SiteBinding.Map;
            MyCamera cam = MySector.MainCamera;
            if (ch == null) { _whyNot = "no character"; HideHint(); return; }
            if (ch.IsDead) { _whyNot = "dead"; HideHint(); return; }
            if (map == null) { _whyNot = "no site map"; HideHint(); return; }
            if (cam == null) { _whyNot = "no camera"; HideHint(); return; }
            if (s.ControlledEntity != ch) { _whyNot = "not controlling the character (" + (s.ControlledEntity != null ? s.ControlledEntity.GetType().Name : "null") + ")"; HideHint(); return; }
            if (S8aChartManager.InTransition) { _whyNot = "chart in transition"; HideHint(); return; }
            if (ch.EquippedTool != null) { _whyNot = "tool in hand (" + ch.EquippedTool.GetType().Name + ")"; HideHint(); return; }   // empty hands only
 //  F is SE's own use key: never catch while SE would use something (a door, a panel, a seat, a floating
 // item): MyCharacter.GetDetectorComponent, MyCharacterDetectorComponent.UseObject
            MyCharacterDetectorComponent det = ch.GetDetectorComponent();
            if (det != null && det.UseObject != null) { UseObjectSkips++; _whyNot = "SE use object in front (" + det.UseObject.GetType().Name + ")"; HideHint(); return; }
            MatrixD cm = cam.WorldMatrix;
            Vec3 c = new Vec3(cm.Translation.X, cm.Translation.Y, cm.Translation.Z);
            Vector3D fw = Vector3D.Normalize(cm.Forward);
            Vec3 f = new Vec3(fw.X, fw.Y, fw.Z);
            Vector3D h = ch.GetHeadMatrix(false).Translation;
            Vec3 eye = new Vec3(h.X, h.Y, h.Z);
            double lead = S17Aim.LeadS((nowMs - _c9ReadMs) / 1000.0);
            double best = double.MaxValue;
            {
                for (int i = 0; i < _c9Count; i++)
                {
                    SnCreature r = S17C9.Row(_c9, i);
                    if ((r.flags & S17Conventions.C9Flags.Catchable) == 0) continue;
                    S17Kind k;
                    if (!S17Conventions.TryKind(r.techType, out k)) continue;
                    Vec3 sn = new Vec3(r.posSn.x + r.velSn.x * lead, r.posSn.y + r.velSn.y * lead, r.posSn.z + r.velSn.z * lead);
                    Vec3 p = map.SnToSe(sn);
                    double reach;
                    bool ok = S17Aim.Qualifies(c, f, eye, p, out reach);
                    if (_nearReach < 0 || reach < _nearReach)
                    {
                        _nearReach = reach; _nearKind = k.Display;
                        Vec3 d = p - c; double t = Vec3.Dot(d, f);
                        _nearPerp = t > 0 ? (d - f * t).Length : -1;
                    }
                    if (!ok || reach >= best) continue;
                    best = reach;
                    _target = r;
                    _target.posSn = new F32x3((float)sn.X, (float)sn.Y, (float)sn.Z);
                    _targetKind = k.Display;
                    _hasTarget = true;
                }
            }
            if (_hasTarget) { _whyNot = "target " + _targetKind; ShowHint("F: catch the " + _targetKind, _target.instanceId, nowMs); }
            else
            {
                _whyNot = _c9Count == 0 ? "no fish in C9 (Subnautica found none within 20 m)" : _nearReach < 0 ? "no kind row among " + _c9Count + " C9 rows" :
                          "nearest " + _nearKind + " " + _nearReach.ToString("0.00", CultureInfo.InvariantCulture) + " m from the eye, " +
                          (_nearPerp < 0 ? "behind the camera" : _nearPerp.ToString("0.00", CultureInfo.InvariantCulture) + " m off the aim line") + " (reach 2.5 m, aim 0.6 m)";
                HideHint();
            }
        }

        private static bool UsePressed()
        {
            IMyInput i = MyInput.Static;
            if (i == null || !i.IsNewGameControlPressed(MyControlsSpace.USE)) return false;
            return MyScreenManager.GetScreenWithFocus() is MyGuiScreenGamePlay && MyGuiScreenGamePlay.ActiveGameplayScreen == null;
        }

        // ================================================================================================ request and answer

        private static void Request(ulong nowMs)
        {
            SnCreature t = _target;
            int why = Book.TryRequest(nowMs, t.instanceId, t.techType, t.posSn.x, t.posSn.y, t.posSn.z, Phases.WorldEpoch);
            if (why != 0)
            {
                Refused++;
                var r = new S17Request { ReqSeq = 0, InstanceId = t.instanceId, TechType = t.techType };
                Row("refused", r, null, S17Conventions.Refusal.Name(why), 0, null);
                return;
            }
            Requests++;
            Row("request", Book.Current, null, null, 0, null);
            HideHint();
        }

        private static void OnAck(ref S17Ack a, ulong nowMs)
        {
            S17Request req = Book.Current;
            ulong sent = Book.SentMs;
            S17CatchBook.AckOutcome o = Book.OnAck(ref a);
            if (o == S17CatchBook.AckOutcome.None) return;
            string result = S17Conventions.Result.Name(a.Result);
            if (o == S17CatchBook.AckOutcome.Late)
            {
                Late++;
                var lr = new S17Request { ReqSeq = a.AckSeq, InstanceId = a.InstanceId, TechType = a.TechType };
                Row("late", lr, result, S17Conventions.Refusal.Name(S17Conventions.Refusal.Late), 0, null);
                DeLog.Info("DE.S17 late answer " + result + " to catch request " + a.AckSeq + (a.Result == S17Conventions.Result.Caught ? " (the fish is gone: the item is given now)" : ""));
                if (a.Result != S17Conventions.Result.Caught) return;
                //  Subnautica removed the fish; the item is still given (never a fish lost without its item)
                S17Kind lk;
                S17Conventions.TryKind(a.TechType, out lk);
                string lwhere;
                string lerr = Give(lk, out lwhere);
                if (lerr != null) { NoItem++; Row("refused", lr, result, S17Conventions.Refusal.Name(S17Conventions.Refusal.NoItem), 0, lerr); return; }
                LateGiven++;
                if (lwhere == "inventory") Given++; else Dropped++;
                Row("given", lr, result, null, 0, lwhere);
                Notify(lwhere == "inventory" ? lk.Display + " (raw) added" : lk.Display + " (raw) dropped at your feet (inventory full)", "Green");
                return;
            }
            ulong ms = nowMs - sent;
            Row("ack", req, result, null, ms, null);
            S17Kind k;
            S17Conventions.TryKind(req.TechType, out k);
            if (a.Result != S17Conventions.Result.Caught)
            {
                Refused++;
                Row("refused", req, result, S17Conventions.Refusal.Name(S17Conventions.Refusal.NotCaught), ms, null);
                Notify("The " + (k.Display ?? "fish") + " got away", "Red");
                return;
            }
            Caught++;
            string where;
            string err = Give(k, out where);
            if (err != null)
            {
                NoItem++;
                Row("refused", req, result, S17Conventions.Refusal.Name(S17Conventions.Refusal.NoItem), ms, err);
                DeLog.Info("DE.S17 caught a " + k.Display + " but no item: " + err);
                Notify("Caught a " + k.Display + ", but its item is missing (world mod)", "Red");
                return;
            }
            if (where == "inventory") Given++; else Dropped++;
            Row("given", req, result, null, nowMs - sent, where);
            Notify(where == "inventory" ? k.Display + " (raw) added" : k.Display + " (raw) dropped at your feet (inventory full)", "Green");
        }

        /// <summary>One raw item of the kind into the local character's inventory, else at its feet; null or the reason.</summary>
        private static string Give(S17Kind k, out string where)
        {
            where = "none";
            if (k.Name == null) return "not a kind";
            MySession s = MySession.Static;
            MyCharacter ch = s != null ? s.LocalCharacter : null;
            if (ch == null) return "no character";
            var id = new MyDefinitionId(typeof(MyObjectBuilder_ConsumableItem), k.RawSubtype);
            MyPhysicalItemDefinition def;
            MyDefinitionManager dm = MyDefinitionManager.Static;
            if (dm == null || !dm.TryGetPhysicalItemDefinition(id, out def)) return "no definition " + k.RawSubtype + " (the world mod's PhysicalItems_DE_Food.sbc)";
            var ob = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_ConsumableItem>(k.RawSubtype);
            MyInventory inv = ch.GetInventory(0);
            if (inv != null && inv.CanItemsBeAdded((MyFixedPoint)1, id) && inv.AddItems((MyFixedPoint)1, ob)) { where = "inventory"; return null; }
            MatrixD m = ch.WorldMatrix;
            Vector3D at = m.Translation + m.Forward * 0.5 + m.Up * 0.5;
            MyFloatingObjects.Spawn(new MyPhysicalInventoryItem((MyFixedPoint)1, ob, 1f), at, m.Forward, m.Up, ch.Physics, null);
            where = "dropped";
            return null;
        }

        // ================================================================================================ HUD and rows

 /// <summary>The target's HUD line: shown when the target changes, refreshed every 250 ms (alive 400 ms), not every tick.</summary>
        private static void ShowHint(string text, uint forInstance, ulong nowMs)
        {
            if (_hintFor == forInstance && nowMs < _nextHintMs) return;
            _nextHintMs = nowMs + 250;
            try
            {
                IMyUtilities u = MyAPIGateway.Utilities;
                if (u == null) return;
                if (_hint == null) _hint = u.CreateNotification(text, 400, "White");
                if (_hintFor != forInstance || _hint.Text != text) { _hint.Text = text; _hintFor = forInstance; }
                _hint.AliveTime = 400;
                _hint.Show();
            }
            catch (Exception) { }
        }

        private static void HideHint()
        {
            if (_hintFor == 0) return;
            _hintFor = 0;
            try { if (_hint != null) _hint.Hide(); } catch (Exception) { }
        }

        private static void Notify(string text, string font)
        {
            try { IMyUtilities u = MyAPIGateway.Utilities; if (u != null) u.ShowNotification(text, 2500, font); } catch (Exception) { }
        }

        private static void Row(string phase, S17Request r, string result, string reason, ulong ms, string where)
        {
            S17Kind k;
            S17Conventions.TryKind(r.TechType, out k);
            var sb = new StringBuilder(256);
            sb.Append("{\"t\":"); JsonText.AppendString(sb, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            sb.Append(",\"ev\":\"s17.catch\"");
            S5Rows.Str(sb, "phase", phase);
            S5Rows.Int(sb, "seq", r.ReqSeq);
            S5Rows.Int(sb, "instance", r.InstanceId);
            S5Rows.Int(sb, "techType", r.TechType);
            S5Rows.Str(sb, "kind", k.Name ?? "");
            if (result != null) S5Rows.Str(sb, "result", result);
            if (reason != null) S5Rows.Str(sb, "reason", reason);
            if (where != null) S5Rows.Str(sb, "where", where);
            S5Rows.Int(sb, "ms", (long)ms);
            S5Rows.Str(sb, "item", k.Name != null ? k.RawSubtype : "");
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
        }

        private static void StatusRow(ulong nowMs, bool up)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"t\":"); JsonText.AppendString(sb, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            sb.Append(",\"ev\":\"s17.status\"");
            S5Rows.Str(sb, "mode", Mode);
            S5Rows.Bool(sb, "up", up);
            S5Rows.Int(sb, "c9Rows", _c9Count);
            S5Rows.Int(sb, "c9Reads", C9Reads);
            S5Rows.Int(sb, "c9Stale", C9Stale);
            S5Rows.Int(sb, "c9Seq", _c9Seq);
            S5Rows.Int(sb, "c9AgeMs", _c9ChangedMs == 0 ? -1 : (long)(nowMs - _c9ChangedMs));
            S5Rows.Str(sb, "why", _whyNot);
            S5Rows.Str(sb, "nearKind", _nearKind);
            S5Rows.Num(sb, "nearReachM", _nearReach);
            S5Rows.Num(sb, "nearPerpM", _nearPerp);
            S5Rows.Int(sb, "requests", Requests);
            S5Rows.Int(sb, "caught", Caught);
            S5Rows.Int(sb, "given", Given);
            S5Rows.Int(sb, "refused", Refused);
            S5Rows.Int(sb, "timeouts", Timeouts);
            S5Rows.Int(sb, "useObjectSkips", UseObjectSkips);
            S5Rows.Int(sb, "errors", Errors);
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
        }

        public static string Status()
        {
            return "S17 " + Mode + ", C9 rows " + _c9Count + " (reads " + C9Reads + ", stale " + C9Stale + "), requests " + Requests + ", caught " + Caught +
                   ", given " + Given + ", dropped " + Dropped + ", refused " + Refused + ", timeouts " + Timeouts + ", late " + Late + " (given " + LateGiven + "), noItem " + NoItem + ", useObject skips " + UseObjectSkips +
                   ", pending " + Book.Pending + ", errors " + Errors + (_lastError != null ? " (" + _lastError + ")" : "");
        }

        /// <summary><c>de.s17 {status}</c> (dev builds; release refuses it: not in UserCommandRules). Chat: <c>/de s17 status</c>.</summary>
        public static Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            if (cmd != Verb) return null;
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            if (!BuildFlavor.IsDev) { d["ok"] = false; d["error"] = "developer command, not in the release build"; return d; }
            d["ok"] = true;
            d["enabled"] = S17Switch.Enabled;
            d["mode"] = Mode;
            d["c9Rows"] = (long)_c9Count;
            d["requests"] = Requests;
            d["caught"] = Caught;
            d["given"] = Given;
            d["dropped"] = Dropped;
            d["refused"] = Refused;
            d["timeouts"] = Timeouts;
            d["late"] = Late;
            d["noItem"] = NoItem;
            d["pending"] = Book.Pending;
            d["errors"] = Errors;
            d["conventions"] = S17Conventions.Header + " 0x" + S17Conventions.Hash().ToString("x16", CultureInfo.InvariantCulture);
            d["line"] = Status();
            return d;
        }
    }
}
