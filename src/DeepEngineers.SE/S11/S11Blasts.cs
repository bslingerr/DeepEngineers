using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using VRage.Game.Entity;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// VERIFIED in the
 /// decompiled SE (MyWarhead.cs): a warhead chain-detonates only when armed (<c>CanExplode</c> = IsFunctional &amp;&amp;
 /// IsArmed); an armed warhead destroyed by a neighbour explodes after a random 0-500 ms (<c>OnDestroy</c> →
 /// <c>MarkForExplosion</c> + <c>ExplodeDelayed(500)</c>,; a bullet: at once); each explosion is one
 /// <c>MyExplosions.AddExplosion</c> with <c>WarheadExplosionDamage</c> (15,000) and a sphere that grows 2.4 % per warhead in
 /// range, ≤ 30 m (<c>GetExplosionSphere</c>). Nothing of ours changes that path: the S7 explosion hook is a postfix
 /// that only reads <c>MyExplosionInfo</c>, the S6 consumer damages blocks only for creature records, the S7p depth hold
 /// only adds forces. What changed is the picture's input: the S7 producer raised one Explosion row per AddExplosion and
 /// the window's per-tick cap could drop rows of a big chain. Now the explosions of one tick are grouped here
 /// (<see cref="S11BlastRules.Group"/>: join within max(R, r) + 2 m, yields summed, R = cbrt(Σr³), ≤ 16 groups, nothing
 /// dropped) and each group is raised as ONE Explosion row with <c>extra</c> = its summed yield (CONTRACT §5.4
 /// <c>format event.yield</c>). Rows <c>s11.blast</c> name the warheads of each group (the chain is visible in the log).
 /// Game thread (AddExplosion postfix, the C2 writer's hook); a lock guards the batch.
 /// </summary>
    internal static class S11Blasts
    {
        private sealed class Pending
        {
            public S11Blast B;
            public byte Type;
            public int Cls;
            public bool Warhead, Synthetic;
        }

        private static readonly object Gate = new object();
        private static readonly List<Pending> Batch = new List<Pending>();
        private static readonly List<S11Blast> Scratch = new List<S11Blast>();
        public const int MaxPending = 4096;
        public static long Explosions, Warheads, Groups, Rows, Compactions, Synthetic;

        /// <summary>
 /// From the S7 explosion postfix (one hook line in EffectsPatches): true = S11 takes the explosion (raised at the
 /// tick's flush as part of a group); false = S11 off (S7 raises it as before).
 /// </summary>
        public static bool Absorb(ref MyExplosionInfo info)
        {
            if (!S11Consumer.Enabled) return false;
            BoundingSphereD s = info.ExplosionSphere;
            bool warhead = info.HitEntity is MyWarhead || info.OwnerEntity is MyWarhead;
            int cls = warhead ? S11Conventions.WeaponClass.Explosion : ClassOfOrigin(info.OriginEntity);
            Add(new S11Blast(s.Center.X, s.Center.Y, s.Center.Z, s.Radius, info.Damage, 0), (byte)info.ExplosionType, warhead, false, cls);
            return true;
        }

        /// <summary>
 /// Review 5: the weapon class of an explosion by its origin. A missile's <c>MyExplosionInfo.OriginEntity</c> is the gun

 /// hand-held launchers, the block or grid for a turret or launcher block. A character -> Handheld, a small grid ->
 /// SmallGrid, anything else (a large grid, a warhead, none) -> Explosion (<c>format event.class</c>).
 /// </summary>
        private static int ClassOfOrigin(long origin)
        {
            if (origin == 0) return S11Conventions.WeaponClass.Explosion;
            MyEntity e;
            if (!MyEntities.TryGetEntityById(origin, out e, true) || e == null) return S11Conventions.WeaponClass.Explosion;
            if (e is MyCharacter) return S11BlastRules.ClassOfOrigin(true, false);
            MyCubeGrid g = e as MyCubeGrid ?? (e is MyCubeBlock ? ((MyCubeBlock)e).CubeGrid : null);
            return S11BlastRules.ClassOfOrigin(false, g != null && g.GridSizeEnum == VRage.Game.MyCubeSize.Small);
        }

        /// <summary> (format missile.hit): the class of a missile's origin entity, as its own explosion's.</summary>
        internal static int ClassOfOriginId(long origin) { return ClassOfOrigin(origin); }

        /// <summary><c>de.s11 stack</c>: N synthetic warhead-sized explosions for the oracle (no SE damage: AddExplosion is not called).</summary>
        public static void AddSynthetic(Vector3D at, int n, double radius, double yield, double spreadM, int cls = S11Conventions.WeaponClass.Explosion)
        {
            for (int i = 0; i < n; i++)
            {
                double a = i * 2.399963; // golden angle: a small disc of explosions
                double r = spreadM * Math.Sqrt((i + 0.5) / Math.Max(1, n));
                Add(new S11Blast(at.X + r * Math.Cos(a), at.Y, at.Z + r * Math.Sin(a), radius, yield, 0), 0, cls == S11Conventions.WeaponClass.Explosion, true, cls);
            }
        }

        private static void Add(S11Blast b, byte type, bool warhead, bool synthetic, int cls)
        {
            lock (Gate)
            {
                Explosions++;
                if (warhead) Warheads++;
                if (synthetic) Synthetic++;
                Batch.Add(new Pending { B = b, Type = type, Warhead = warhead, Synthetic = synthetic, Cls = cls });
                if (Batch.Count >= MaxPending) Compact();
            }
        }

        /// <summary>A batch that is never flushed (no stream) stays bounded: it is grouped in place.</summary>
        private static void Compact()
        {
            Pending[] all = Batch.ToArray();
            Batch.Clear();
            for (int cls = S11Conventions.WeaponClass.Handheld; cls <= S11Conventions.WeaponClass.Explosion; cls++)
            {
                Scratch.Clear();
                foreach (Pending p in all) if (p.Cls == cls) Scratch.Add(p.B);
                if (Scratch.Count == 0) continue;
                foreach (S11Blast b in S11BlastRules.Group(Scratch, S11Conventions.Blast.MaxGroupsPerTick))
                    Batch.Add(new Pending { B = b, Type = 0, Warhead = cls == S11Conventions.WeaponClass.Explosion, Cls = cls });
            }
            Compactions++;
        }

        /// <summary>From the S7 producer's C2 hook (one line in EffectsProducer.BeforeC2, before the tick closes): the tick's groups raised as rows.</summary>
        public static void Flush(ulong tick)
        {
            Pending[] items;
            lock (Gate)
            {
                if (Batch.Count == 0) return;
                items = Batch.ToArray();
                Batch.Clear();
            }
            int warheads = 0, synthetic = 0;
            foreach (Pending p in items)
            {
                if (p.Warhead) warheads++;
                if (p.Synthetic) synthetic++;
            }
            var groups = new List<S11Blast>();
            var sb = new StringBuilder(256);
            for (int cls = S11Conventions.WeaponClass.Handheld; cls <= S11Conventions.WeaponClass.Explosion; cls++)
            {
                //  groups never mix weapon classes (a hand-held rocket keeps its floor, a warhead does not)
                Scratch.Clear();
                byte type = 0;
                double biggest = -1;
                foreach (Pending p in items)
                {
                    if (p.Cls != cls) continue;
                    Scratch.Add(p.B);
                    if (p.B.Yield > biggest) { biggest = p.B.Yield; type = p.Type; }
                }
                if (Scratch.Count == 0) continue;
                foreach (S11Blast g in S11BlastRules.Group(Scratch, S11Conventions.Blast.MaxGroupsPerTick))
                {
                    groups.Add(g);
                    S7Effects.OnExplosion(new BoundingSphereD(new Vector3D(g.X, g.Y, g.Z), g.R), type, (float)Math.Min(g.Yield, float.MaxValue), S11BlastRules.YieldCode(g.Yield),
                                          S11BlastRules.ClassBits(cls)); // one row: extra = the summed yield, subtype bits 8-11 = the class
                    Rows++;
                    sb.Append(sb.Length > 0 ? "," : "").Append("{\"yield\":").Append(S11SeText.G(g.Yield)).Append(",\"radius\":").Append(S11SeText.G(g.R))
                      .Append(",\"count\":").Append(g.Count.ToString(CultureInfo.InvariantCulture)).Append(",\"sumR3\":").Append(S11SeText.G(g.SumR3))
                      .Append(",\"cls\":\"").Append(S11Conventions.WeaponClass.Name(cls)).Append("\"}");
                }
            }
            Groups += groups.Count;
            var row = new StringBuilder(384);
            row.Append("{\"t\":"); JsonText.AppendString(row, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            row.Append(",\"ev\":\"s11.blast\"");
            S5Rows.Int(row, "tick", (long)tick);
            S5Rows.Int(row, "n", items.Length);
            S5Rows.Int(row, "warheads", warheads);
            S5Rows.Int(row, "synthetic", synthetic);
            row.Append(",\"groups\":[").Append(sb).Append(']');
            Jsonl.WriteLineBuffered(S5Rows.End(row));
            if (items.Length > 1 || warheads > 0)
                DeLog.Info("DE.S11 blast tick " + tick + ": " + items.Length + " explosion(s), " + warheads + " warhead(s)" + (synthetic > 0 ? " (" + synthetic + " synthetic)" : "") +
                           " -> " + groups.Count + " row(s), yield " + S11SeText.G(groups.Count > 0 ? groups[0].Yield : 0) + " radius " + (groups.Count > 0 ? groups[0].R.ToString("0.#", CultureInfo.InvariantCulture) : "0") + " m");
        }

        public static Dictionary<string, object> Status()
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            lock (Gate) d["pending"] = Batch.Count;
            d["explosions"] = Explosions;
            d["warheads"] = Warheads;
            d["groups"] = Groups;
            d["rows"] = Rows;
            d["synthetic"] = Synthetic;
            d["compactions"] = Compactions;
            return d;
        }
    }
}
