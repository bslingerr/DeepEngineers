using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.SE.Combat;
using DeepEngineers.Wire;
using Sandbox.Game.World;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// SE <c>de.s11</c> (C11 and chat <c>/de s11 …</c>, dev builds; the verb is not in the release allow-list):
 /// <list type="bullet">
 /// <item><c>{status}</c> (default): the consumer's and the explosion grouper's counters;</item>
 /// <item><c>{stack: n, radius?, yield?, ahead?, spread?, cls?}</c>: n synthetic warhead-sized explosions (15,000, 22.44 m) at
 /// <c>ahead</c> m in front of the camera, grouped and raised like real ones (no SE damage: AddExplosion is not called) — the
 /// S11.A3 oracle without placing warheads;</item>
 /// <item><c>{release: true}</c>: every held grid let go;</item>
 /// <item><c>{volley: {sn, count, kind, from, every, origin}}</c>: SE's own missiles fired at a Subnautica point (S11Volley);</item>
 /// <item><c>{guard: true|false}</c>: the harness guard (SE's admin invulnerability on the local player, restored on false
 /// and on unload; S11Guard);</item>
 /// <item><c>{inject: {unit, amount, type, target: "character"|id, kind?, seq?, gen?, key?, tech?, w?, dir?, up?, section?}, apply}</c>: one S11 record through the same
 /// path as C8's (a dry run unless <c>apply</c>; an applied one needs an explicit seq while the link is up, as de.s6).</item>
 /// </list>
 /// </summary>
    internal static class S11Commands
    {
        public const string Verb = "de.s11";

        public static Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            if (cmd != Verb) return null;
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                object v;
                if (args != null && args.TryGetValue("stack", out v))
                {
                    int n = Math.Max(1, Math.Min(10000, Convert.ToInt32(v, CultureInfo.InvariantCulture)));
                    double radius = Num(args, "radius", 22.4415), yield = Num(args, "yield", 15000), ahead = Num(args, "ahead", 60), spread = Num(args, "spread", 1.5);
                    var cam = Sandbox.Game.World.MySector.MainCamera;
                    if (cam == null) { d["ok"] = false; d["error"] = "no camera"; return d; }
                    Vector3D at = cam.Position + (Vector3D)cam.ForwardVector * ahead;
                    int cls = args.TryGetValue("cls", out v) ? S11Conventions.WeaponClass.Parse(Convert.ToString(v, CultureInfo.InvariantCulture)) : 0;
                    if (cls == 0) cls = S11Conventions.WeaponClass.Explosion;
                    S11Blasts.AddSynthetic(at, n, radius, yield, spread, cls);
                    DeLog.Info("DE.S11 stack: " + n + " synthetic explosion(s) of " + S11SeText.G(yield) + " / " + S11SeText.G(radius) + " m queued " + S11SeText.G(ahead) + " m ahead (" + via + ")");
                    d["ok"] = true;
                    d["queued"] = n;
                    d["cls"] = S11Conventions.WeaponClass.Name(cls);
                    d["at"] = new object[] { at.X, at.Y, at.Z };
                    return d;
                }
                object act;
                bool biteAction = args != null && args.TryGetValue("action", out act) && Convert.ToString(act, CultureInfo.InvariantCulture) == "bite";
                if (args != null && (args.TryGetValue("bite", out v) || biteAction))
                {
                    // de.s11 {bite: {creature, mult}} or {action: bite, creature, mult} (chat /de s11 bite creature=ReaperLeviathan mult=3), {.., reset}
                    var b = (v as IDictionary<string, object>) ?? args;
                    if (b.ContainsKey("reset")) { S11Consumer.Bites.Reset(); d["ok"] = true; d["bites"] = S11Consumer.Bites.Describe(); return d; }
                    object cv;
                    string cname = b.TryGetValue("creature", out cv) ? Convert.ToString(cv, CultureInfo.InvariantCulture) : null;
                    S11ScaleRow row;
                    int tech = cname != null && S11Conventions.TryScaleRow(cname, out row) ? row.TechType : (int)Num(b, "creature", 0);
                    string error;
                    if (!S11Consumer.Bites.Set(tech, null, Num(b, "mult", double.NaN), out error)) { d["ok"] = false; d["error"] = error; return d; }
                    DeLog.Info("DE.S11 bite " + cname + " x" + S11Consumer.Bites.BiteMult(tech).ToString("0.###", CultureInfo.InvariantCulture) + " on grids (de.s11; the pinned default is unchanged)");
                    d["ok"] = true;
                    d["bites"] = S11Consumer.Bites.Describe();
                    return d;
                }
                if (args != null && args.TryGetValue("guard", out v))
                {
                    // pass 4: the harness guard (S11Guard; dev only, like every de.s11 verb)
                    string sv = Convert.ToString(v, CultureInfo.InvariantCulture);
                    bool on = v is bool ? (bool)v : sv == "true" || sv == "on" || sv == "True";
                    string line = S11Guard.Set(on, via);
                    d["ok"] = S11Guard.On == on;
                    d["guard"] = S11Guard.On;
                    d["line"] = line;
                    d["character"] = S11Guard.CharacterState();
                    return d;
                }
                if (args != null && args.TryGetValue("release", out v))
                {
                    S11Consumer.ReleaseAll("de.s11 release");
                    d["ok"] = true;
                    return d;
                }
                if (args != null && args.TryGetValue("volley", out v))
                {
                    //: the scripted volley of the S11.R rows (S11Volley; SE's own missiles at a point Subnautica names)
                    return S11Volley.Queue(v as IDictionary<string, object>, Wire.Native.Win32.TickCount64());
                }
                if (args != null && args.TryGetValue("inject", out v))
                {
                    var a = v as IDictionary<string, object>;
                    if (a == null) { d["ok"] = false; d["error"] = "inject: {unit, amount, type, target, kind?, seq?, gen?}"; return d; }
                    var r = default(DamageRecord);
                    r.unit = (uint)Num(a, "unit", S11Conventions.Unit.HazardHp);
                    r.amount = (float)Num(a, "amount", 1);
                    r.snDamageType = (ushort)Num(a, "type", 1);
                    object t;
                    long target = 0;
                    bool character = !a.TryGetValue("target", out t) || Convert.ToString(t, CultureInfo.InvariantCulture) == "character";
                    if (character) target = CharacterExport.CurrentCharacterId;
                    else if (t is double && Math.Abs((double)t) > 9007199254740992.0) { d["ok"] = false; d["error"] = "inject.target: send an entity id as a string (a JSON number above 2^53 loses digits)"; return d; }
                    else target = Convert.ToInt64(t, CultureInfo.InvariantCulture);
                    r.targetKind = (uint)Num(a, "kind", character ? DamageTargetKind.Character : DamageTargetKind.GridSection);
                    r.targetEntityId = r.rootEntityId = (ulong)target;
                    r.dedupSeq = a.ContainsKey("seq") ? (uint)Num(a, "seq", 0) : DamageConsumer.NextInjectSeq();
                    uint gen, epoch;
                    DamageConsumer.Defaults(Plugin.Instance != null ? Plugin.Instance.LinkForS1 : null, out gen, out epoch);
                    r.snGeneration = a.ContainsKey("gen") ? (uint)Num(a, "gen", gen) : gen;
                    r.worldEpoch = epoch;
                    r.attackDirSn = V3(a, "dir", new F32x3(1, 0, 0));
                    r.localHit = V3(a, "up", new F32x3(0, 1, 0));
                    r.snWorldHit = V3(a, "w", new F32x3(0, 0, 0));
                    r.sectionId = (uint)Num(a, "section", 0);
                    r.dealerInstanceId = (uint)Num(a, "key", 1);
                    r.dealerTechType = (int)Num(a, "tech", 2540);
                    r.snTimeSec = Num(a, "snTime", 0); // 20261024-030926: the sender's clock, for the mouth velocity (S11se's forced grab)
                    bool apply = args.TryGetValue("apply", out t) && (t is bool ? (bool)t : Convert.ToString(t, CultureInfo.InvariantCulture) == "true");
                    Dictionary<string, object> res;
                    bool ok = DamageConsumer.Inject(r, apply, out res, a.ContainsKey("seq"));
                    d["ok"] = ok;
                    d["result"] = res;
                    return d;
                }
                d["ok"] = true;
                d["consumer"] = S11Consumer.Status();
                d["blasts"] = S11Blasts.Status();
                d["line"] = "DE.S11 " + (S11Consumer.Enabled ? "on" : "OFF (" + S11Consumer.EnvVar + ")") + ": hazards " + S11Consumer.AppliedHazard + ", heals " + S11Consumer.AppliedHeal +
                            ", oxygen " + S11Consumer.AppliedOxygen + ", EMP " + S11Consumer.AppliedEmp + ", grabs " + S11Consumer.GrabStarts + " (" + S11Consumer.GrabReleases + " released), pushes " +
                            S11Consumer.Pushes + ", explosions " + S11Blasts.Explosions + " in " + S11Blasts.Rows + " row(s)";
                return d;
            }
            catch (Exception e)
            {
                DeLog.Info("DE.Error de.s11 " + e.GetType().Name + ": " + e.Message + " " + e.StackTrace);
                d["ok"] = false;
                d["error"] = "de.s11 failed: " + e.GetType().Name + ": " + e.Message;
                return d;
            }
        }

        private static F32x3 V3(IDictionary<string, object> a, string k, F32x3 dflt)
        {
            object v;
            if (a == null || !a.TryGetValue(k, out v)) return dflt;
            var l = v as System.Collections.IList;
            if (l == null || l.Count != 3) return dflt;
            try { return new F32x3(Convert.ToSingle(l[0], CultureInfo.InvariantCulture), Convert.ToSingle(l[1], CultureInfo.InvariantCulture), Convert.ToSingle(l[2], CultureInfo.InvariantCulture)); }
            catch (Exception) { return dflt; }
        }

        private static double Num(IDictionary<string, object> a, string k, double dflt)
        {
            object v;
            if (a == null || !a.TryGetValue(k, out v) || v == null) return dflt;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch (Exception) { return dflt; }
        }
    }
}
