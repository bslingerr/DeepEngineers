using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeepEngineers.SE.Combat;
using DeepEngineers.Wire;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S11 "Creatures and hazards", SE side (CONTRACT §5.4 "S11 conventions"): the consumer of the C8 records whose unit is
 /// 17-22, called by S6's drain (<see cref="DamageConsumer"/>, one hook line) with S6's own dedup window, so S6 and S11
 /// records are deduplicated together (exactly once). Health and physics stay SE's: hazards and heals go through
 /// <c>MyCharacter.DoDamage</c> and <c>MyEntityStat.Increase</c>, oxygen through <c>MyCharacterOxygenComponent.SuitOxygenAmount</c>,
 /// the EMP switches the grid's own power producers off and on again, a held grid follows the creature's mouth by forces
 /// never a teleport, and a bite on a grid the
 /// creature cannot hold pushes it by an impulse. Members: <c>MyCharacter.DoDamage(float,
 /// MyStringHash, bool, long, MyStringHash?)</c>, <c>CharactersCanDie</c>, <c>CharacterCanDie</c>, <c>StatComp.Health</c>
 /// <c>IsDead</c>
 /// <c>OxygenComponent</c> <c>.SuitOxygenAmount</c>
 /// and <c>.OxygenCapacity</c>, <c>MyCubeGrid.GetFatBlocks</c>,
 /// <c>MyFunctionalBlock.Enabled</c>, <c>MyResourceSourceComponent.ResourceTypes</c>
 /// <c>MyResourceDistributorComponent.ElectricityId</c>, <c>MyGravityProviderSystem.CalculateNaturalGravityInPoint(Vector3D)</c>
 /// <c>PositionComp.LocalAABB</c>. Game thread (Plugin.UpdateCore).
 /// </summary>
    internal static class S11Consumer
    {
        public const string EnvVar = "DEEPENGINEERS_S11";
        public static readonly bool Enabled = S11Read();

        private static bool S11Read()
        {
            try
            {
                string v = Environment.GetEnvironmentVariable(EnvVar);
                if (string.IsNullOrEmpty(v)) return true;
                v = v.Trim().ToLowerInvariant();
                return !(v == "off" || v == "0" || v == "false" || v == "no");
            }
            catch (Exception) { return true; }
        }

        private static readonly S11CapBook Caps = new S11CapBook();
        private static readonly S11EmpBook Emp = new S11EmpBook();
        private static readonly S11GrabBook Grabs = new S11GrabBook();
        /// s11 {bite: {creature, mult}}</c>.</summary>
        public static readonly S11ScaleTable Bites = new S11ScaleTable();
        public static long BiteMultiplied;
        private static readonly Dictionary<long, MyFunctionalBlock> EmpBlocks = new Dictionary<long, MyFunctionalBlock>();
        private static readonly List<long> Scratch = new List<long>();
        public static readonly long[] Rejected = new long[32];
        public static long HoldSnaps, SurfaceReleases, SurfacedDrops, MissileHits, MissileGone, MissileSlugs;
        /// <summary>format surface.release: grab keys SE let go at the surface (their later hold and release records are dropped quietly).</summary>
        private static readonly Dictionary<uint, ulong> Surfaced = new Dictionary<uint, ulong>();
        public static long Drained, Applied, AppliedHazard, AppliedHeal, AppliedOxygen, AppliedEmp, GrabStarts, GrabHolds, GrabReleases, Pushes, HoldForces,
                           Errors, NotApplied;
        public static double HazardSe, HealSe, OxygenL;
        private static RateLimit _errorLog = new RateLimit(10000); // a mutable struct: never readonly (a readonly copy never advances)
        private static RateLimit _holdRow = new RateLimit(1000); // hold rows came from the readonly copy

        private static string Utc() { return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); }

        private static StringBuilder Head(string name)
        {
            var sb = new StringBuilder(384);
            sb.Append("{\"t\":"); JsonText.AppendString(sb, Utc());
            sb.Append(",\"ev\":"); JsonText.AppendString(sb, "s11." + name);
            return sb;
        }

        // ============================================================================================= one record

 /// <summary>
 /// From <see cref="DamageConsumer"/> for a unit 17-22: S6's header and dedup checks (their reasons returned for S6's
 /// counters), then S11's payload, target and gate, then the application. <paramref name="dedup"/> null = a dry run.
 /// </summary>
        public static DamageReject Process(DamageRecord r, uint snGeneration, uint worldEpoch, DedupWindow dedup, bool apply, bool injected, ulong nowMs,
                                           Dictionary<string, object> result)
        {
            DamageReject h = DamageRules.CheckHeader(r, snGeneration, worldEpoch);
            if (h != DamageReject.None) return h;
            if (dedup != null)
            {
                h = dedup.Admit(r.dedupSeq);
                if (h != DamageReject.None) return h;
            }
            else if (r.dedupSeq == 0) return DamageReject.Sequence;
            Drained++;
            string id = DamageRules.HitId(r.snGeneration, r.dedupSeq);
            if (result != null)
            {
                result["id"] = id;
                result["unit"] = S11Conventions.Unit.Name(r.unit);
            }
            if (!Enabled) return Reject(r, id, S11Reject.Off, "DEEPENGINEERS_S11=off", result);
            S11Reject x = S11Records.CheckPayload(r);
            if (x != S11Reject.None) return Reject(r, id, x, "payload", result);
            string detail = null;
            try
            {
                switch (r.unit)
                {
                    case S11Conventions.Unit.HazardHp: x = Hazard(r, id, apply, nowMs, out detail); break;
                    case S11Conventions.Unit.HealHp: x = Heal(r, id, apply, nowMs, out detail); break;
                    case S11Conventions.Unit.OxygenS: x = Oxygen(r, id, apply, out detail); break;
                    case S11Conventions.Unit.EmpS: x = EmpStart(r, id, apply, nowMs, out detail); break;
                    case S11Conventions.Unit.Grab: x = Grab(r, id, apply, nowMs, result, out detail); break;
                    case S11Conventions.Unit.Push: x = Push(r, id, apply, out detail); break;
                    case S11Conventions.Unit.MissileHit: x = MissileHit(r, id, apply, nowMs, out detail); break;
                    default: x = S11Reject.Unit; break;
                }
            }
            catch (Exception e)
            {
                Errors++;
                if (_errorLog.Allow(nowMs)) DeLog.Info("DE.Error S11 record " + id + " " + e.GetType().Name + ": " + e.Message + " " + e.StackTrace);
                x = S11Reject.NotApplied;
                detail = "exception " + e.GetType().Name;
            }
            if (x != S11Reject.None) return Reject(r, id, x, detail, result);
            if (result != null) { result["outcome"] = apply ? "applied" : "would apply"; if (detail != null) result["detail"] = detail; }
            if (apply) Applied++;
            return DamageReject.None;
        }

        private static DamageReject Reject(DamageRecord r, string id, S11Reject why, string detail, Dictionary<string, object> result)
        {
            int i = (int)why;
            if (i >= 0 && i < Rejected.Length) Rejected[i]++;
            if (result != null) { result["outcome"] = "rejected"; result["reason"] = why.ToString(); result["detail"] = detail; }
            var sb = Head("reject");
            S5Rows.Str(sb, "id", id);
            S5Rows.Str(sb, "unit", S11Conventions.Unit.Name(r.unit));
            S5Rows.Str(sb, "reason", why.ToString());
            S5Rows.Str(sb, "detail", detail);
            S5Rows.Int(sb, "type", r.snDamageType);
            S5Rows.Num(sb, "amount", r.amount);
            S5Rows.Id(sb, "target", (long)r.targetEntityId);
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
            return DamageReject.None; // S11's own reasons are counted here (s11.reject), not as S6 rejects
        }

        private static MyCharacter CharacterOf(DamageRecord r, out S11Reject why)
        {
            why = S11Reject.None;
            long target = (long)r.targetEntityId;
            long exported = CharacterExport.CurrentCharacterId;
            if (exported == 0 || target != exported || (long)r.rootEntityId != target || r.sectionId != 0) { why = S11Reject.Entity; return null; }
            MyEntity e;
            MyCharacter c = MyEntities.TryGetEntityById(target, out e, false) ? e as MyCharacter : null;
            if (c == null || c.Closed) { why = S11Reject.Entity; return null; }
            return c;
        }

        private static MyCubeGrid GridOf(DamageRecord r, out S11Reject why)
        {
            why = S11Reject.None;
            MyCubeGrid g;
            SentSkin skin;
            if (!CollisionSkin.TryGetGrid((long)r.targetEntityId, out g, out skin) || g == null || g.Closed) { why = S11Reject.Entity; return null; }
            return g;
        }

        // ============================================================================================= D: hazards

        private static S11Reject Hazard(DamageRecord r, string id, bool apply, ulong nowMs, out string detail)
        {
            detail = null;
            S11Reject why;
            MyCharacter c = CharacterOf(r, out why);
            if (c == null) { detail = "not the exported character"; return why; }
            S11HazardRow row;
            if (!S11Conventions.TryHazardRow(r.snDamageType, out row)) return S11Reject.NotForwarded;
            MySession s = MySession.Static;
            string reason;
            if (!CombatGate.Character(s != null && s.CreativeMode, MyCharacter.CharactersCanDie, c.CharacterCanDie, out reason)) { detail = reason; return S11Reject.CannotDie; }
            MyEntityStat health = c.StatComp != null ? c.StatComp.Health : null;
            if (health == null || c.IsDead) { detail = "no health or dead"; return S11Reject.NotApplied; }
            double want = S11HazardRules.SeAmount(r.snDamageType, r.amount);
            if (!apply) { detail = "would deal " + S11SeText.G(want); return S11Reject.None; }
            double grant = Caps.Grant(r.snDamageType, nowMs, want, row.CapPerS);
            double before = health.Value;
            bool ok = grant > 0 && c.DoDamage((float)grant, MyStringHash.GetOrCompute(row.SeType), true, 0L);
            double drop = before - health.Value;
            var sb = Head("hazard");
            S5Rows.Str(sb, "id", id);
            S5Rows.Int(sb, "type", r.snDamageType);
            S5Rows.Str(sb, "seType", row.SeType);
            S5Rows.Num(sb, "amount", r.amount);
            S5Rows.Num(sb, "se", want);
            S5Rows.Num(sb, "granted", grant);
            S5Rows.Num(sb, "applied", drop);
            S5Rows.Num(sb, "hpBefore", before);
            S5Rows.Int(sb, "flags", r.flags);
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
            if (grant <= 0) { detail = "capped (" + row.CapPerS + " HP/s)"; return S11Reject.None; } // counted as capped, not a reject
            if (!ok || !(drop > 0)) { NotApplied++; detail = "MyCharacter.DoDamage applied nothing"; return S11Reject.NotApplied; }
            AppliedHazard++;
            HazardSe += drop;
            if (Caps.NoticeDue(r.snDamageType, nowMs)) Notice(S11SeText.HazardNotice(row, drop));
            return S11Reject.None;
        }

        private static void Notice(string text)
        {
            try
            {
                var u = (IMyUtilities)MyAPIUtilities.Static;
                if (u != null) u.ShowNotification(text, 2000, "Red");
            }
            catch (Exception) { }
        }

        // ============================================================================================= E: heal and oxygen

        private static S11Reject Heal(DamageRecord r, string id, bool apply, ulong nowMs, out string detail)
        {
            detail = null;
            S11Reject why;
            MyCharacter c = CharacterOf(r, out why);
            if (c == null) { detail = "not the exported character"; return why; }
            MyEntityStat health = c.StatComp != null ? c.StatComp.Health : null;
            if (health == null || c.IsDead) { detail = "no health or dead"; return S11Reject.NotApplied; }
            double heal = S11HealRules.SeHeal(r.amount, health.Value, health.MaxValue);
            if (!apply) { detail = "would heal " + S11SeText.G(heal); return S11Reject.None; }
            double grant = Caps.Grant(-1, nowMs, heal, S11Conventions.Heal.CapPerS);
            double before = health.Value;
            if (grant > 0) health.Increase((float)grant, null);
            double rise = health.Value - before;
            var sb = Head("heal");
            S5Rows.Str(sb, "id", id);
            S5Rows.Num(sb, "amount", r.amount);
            S5Rows.Num(sb, "se", heal);
            S5Rows.Num(sb, "applied", rise);
            S5Rows.Num(sb, "hpBefore", before);
            S5Rows.Num(sb, "hpMax", health.MaxValue);
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
            AppliedHeal++;
            HealSe += Math.Max(0, rise);
            if (rise > 0) DeLog.Info("DE.S11 heal id=" + id + " +" + S11SeText.G(rise) + " HP (Subnautica first aid " + S11SeText.G(r.amount) + " SN_HP)");
            return S11Reject.None;
        }

        private static S11Reject Oxygen(DamageRecord r, string id, bool apply, out string detail)
        {
            detail = null;
            S11Reject why;
            MyCharacter c = CharacterOf(r, out why);
            if (c == null) { detail = "not the exported character"; return why; }
            MyCharacterOxygenComponent oc = c.OxygenComponent;
            if (oc == null || c.IsDead) { detail = "no oxygen component or dead"; return S11Reject.NotApplied; }
            double cap = oc.OxygenCapacity;
            double litres = S11OxygenRules.Litres(r.amount, cap);
            if (!apply) { detail = "would add " + S11SeText.G(litres) + " L"; return S11Reject.None; }
            double before = oc.SuitOxygenAmount;
            double next = S11OxygenRules.Next(before, litres, cap);
            if (next > before) oc.SuitOxygenAmount = (float)next;
            double after = oc.SuitOxygenAmount;
            var sb = Head("oxygen");
            S5Rows.Str(sb, "id", id);
            S5Rows.Str(sb, "source", S11Conventions.OxygenSource.Name(r.snDamageType));
            S5Rows.Num(sb, "seconds", r.amount);
            S5Rows.Num(sb, "litres", litres);
            S5Rows.Num(sb, "before", before);
            S5Rows.Num(sb, "after", after);
            S5Rows.Num(sb, "capacity", cap);
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
            AppliedOxygen++;
            OxygenL += Math.Max(0, after - before);
            return S11Reject.None;
        }

        // ============================================================================================= D: EMP

        private static S11Reject EmpStart(DamageRecord r, string id, bool apply, ulong nowMs, out string detail)
        {
            detail = null;
            S11Reject why;
            MyCubeGrid g = GridOf(r, out why);
            if (g == null) { detail = "grid not mirrored"; return why; }
            double seconds = Math.Max(S11Conventions.Emp.MinS, Math.Min(S11Conventions.Emp.MaxS, r.amount));
            int off = 0, already = 0, producers = 0;
            foreach (MyCubeBlock b in g.GetFatBlocks())
            {
                var fb = b as MyFunctionalBlock;
                if (fb == null || fb.Closed || !ProducesElectricity(fb)) continue;
                producers++;
                if (Emp.IsOurs(fb.EntityId)) { if (apply) Emp.Begin(fb.EntityId, g.EntityId, nowMs, seconds); already++; continue; }
                if (!fb.Enabled) continue; // the player's own choice: never touched
                if (!apply) { off++; continue; }
                if (Emp.Begin(fb.EntityId, g.EntityId, nowMs, seconds))
                {
                    S11SavePatch.Ensure(); //  a save inside the power-loss window stores the producer on
                    fb.Enabled = false;
                    EmpBlocks[fb.EntityId] = fb;
                    off++;
                }
            }
            var sb = Head("emp");
            S5Rows.Str(sb, "id", id);
            S5Rows.Id(sb, "grid", g.EntityId);
            S5Rows.Num(sb, "seconds", seconds);
            S5Rows.Int(sb, "producers", producers);
            S5Rows.Int(sb, "off", off);
            S5Rows.Int(sb, "extended", already);
            S5Rows.Bool(sb, "dry", !apply);
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
            if (off + already == 0) { detail = producers + " producers, none enabled"; return S11Reject.NoPower; }
            if (apply)
            {
                AppliedEmp++;
                DeLog.Info("DE.S11 EMP id=" + id + " grid " + g.EntityId + ": " + off + " power producer(s) off for " + S11SeText.G(seconds) + " s (" + already + " extended)");
                Notice("Crabsquid EMP: power lost for " + seconds.ToString("0", CultureInfo.InvariantCulture) + " s");
            }
            return S11Reject.None;
        }

        private static bool ProducesElectricity(MyFunctionalBlock fb)
        {
            MyResourceSourceComponent src;
            if (!fb.Components.TryGet<MyResourceSourceComponent>(out src) || src == null) return false;
            foreach (var t in src.ResourceTypes) if (t == MyResourceDistributorComponent.ElectricityId) return true;
            return false;
        }

        private static void EmpRestore(List<long> keys, string why)
        {
            foreach (long k in keys)
            {
                MyFunctionalBlock fb;
                if (!EmpBlocks.TryGetValue(k, out fb)) continue;
                EmpBlocks.Remove(k);
                try { if (fb != null && !fb.Closed && !fb.MarkedForClose) fb.Enabled = true; } catch (Exception) { }
            }
            if (keys.Count > 0)
            {
                var sb = Head("emp");
                S5Rows.Str(sb, "phase", "restore");
                S5Rows.Int(sb, "on", keys.Count);
                S5Rows.Str(sb, "why", why);
                Jsonl.WriteLineBuffered(S5Rows.End(sb));
            }
        }

        // ============================================================================================= C: grabs

        private static Vec3 V(Vector3D v) { return new Vec3(v.X, v.Y, v.Z); }
        private static Vector3D D(Vec3 v) { return new Vector3D(v.X, v.Y, v.Z); }

        private static Quat QuatOf(MatrixD m)
        {
            Quaternion q = Quaternion.CreateFromRotationMatrix(in m);
            return new Quat(q.X, q.Y, q.Z, q.W).Normalized();
        }

        /// <summary>The attach pose in SE world: the point through the site map, forward and up through mapdir⁻¹ at that point.</summary>
        private static bool AttachSe(DamageRecord r, out Vec3 p, out Quat q)
        {
            p = Vec3.Zero;
            q = Quat.Identity;
            SiteMap map = SiteBinding.Map;
            if (map == null) return false;
            p = map.SnToSe(new Vec3(r.snWorldHit.x, r.snWorldHit.y, r.snWorldHit.z));
            Vec3 f = map.DirSnToSe(p, new Vec3(r.attackDirSn.x, r.attackDirSn.y, r.attackDirSn.z)).Normalized();
            Vec3 u = map.DirSnToSe(p, new Vec3(r.localHit.x, r.localHit.y, r.localHit.z)).Normalized();
            MatrixD w = MatrixD.CreateWorld(D(p), D(f), D(u));
            q = QuatOf(w);
            return true;
        }

        private static S11Reject Grab(DamageRecord r, string id, bool apply, ulong nowMs, Dictionary<string, object> result, out string detail)
        {
            detail = null;
            S11Reject why;
            MyCubeGrid g = GridOf(r, out why);
            if (g == null) { detail = "grid not mirrored"; return why; }
            if ((long)r.targetEntityId != (long)r.rootEntityId) { detail = "a grab holds the root grid"; return S11Reject.Entity; }
            int phase = r.snDamageType;
            uint key = r.dealerInstanceId;
            if (phase == S11Conventions.GrabPhase.Start)
            {
                S11GrabRow row;
                if (!S11Conventions.TryGrabRow(r.dealerTechType, out row)) { detail = "creature " + r.dealerTechType + " has no grab row"; return S11Reject.NotGrabbable; }
                // run 1: the size class first, with the same function on the same bounds as Subnautica (the skin boxes
 // SE sent, S11GrabRules.SkinExtents), so a refusal names the size before the static test; LocalAABB only without a skin
                SentSkin sk;
                Vector3 size;
                bool skin = CollisionSkin.TryGetSkin(g.EntityId, out sk) && sk.HasBox;
                if (skin) size = new Vector3((float)(sk.BoxMax.X - sk.BoxMin.X), (float)(sk.BoxMax.Y - sk.BoxMin.Y), (float)(sk.BoxMax.Z - sk.BoxMin.Z));
                else size = g.PositionComp.LocalAABB.Size;
                bool fits = S11GrabRules.SizeFits(row, size.X, size.Y, size.Z);

                int blocks = g.BlocksCount;
                double volume = (double)size.X * size.Y * size.Z;
                bool debris = S11DebrisRules.IsDebris(blocks, volume);
                if (result != null)
                {
                    result["blocks"] = blocks;
                    result["volumeM3"] = volume;
                    result["debris"] = debris;
                }
                if (debris && !S11SeMutants.NoDebrisFilter)
                {
                    detail = "debris: " + blocks + " block(s), " + S11SeText.G(volume) + " m3 (a remnant is never grabbed)";
                    GrabRow("refused", id, key, g, row.Creature, size, detail);
                    return S11Reject.Debris;
                }
                if (result != null)
                {
                    // pass 4: the measured box and the class in the reply, so S11.C1 judges the class boundary
 // with the twin's size_fits on what SE measured (not a fixed verdict per test grid)
                    result["creature"] = row.Creature;
                    result["size"] = new object[] { (double)size.X, (double)size.Y, (double)size.Z };
                    result["sizeSource"] = skin ? "skin" : "LocalAABB";
                    result["fits"] = fits;
                    result["classVolumeM3"] = row.MaxVolumeM3;
                    result["classEdgeM"] = row.MaxEdgeM;
                    result["static"] = g.IsStatic || g.Physics == null || g.Physics.IsStatic;
                }
                if (!fits && !S11SeMutants.NoSizeClass) { detail = "box " + Size(size) + " m over the " + row.Creature + " size class"; GrabRow("refused", id, key, g, row.Creature, size, detail); return S11Reject.TooLarge; }
                if (g.IsStatic || g.Physics == null || g.Physics.IsStatic) { detail = "static grid (box " + Size(size) + " m fits)"; return S11Reject.Static; }
                //  SetWorldMatrix moves the root grid only; rotor, piston and connector subgrids would lag and be yanked
                int mech = MechanicalCount(g);
                if (mech > 1) { detail = mech + " grids in its mechanical group (rotors, pistons, connectors): never held"; GrabRow("refused", id, key, g, row.Creature, size, "subgrids"); return S11Reject.Subgrids; }
                if (!apply) { detail = "would hold"; return S11Reject.None; }
                S11Held h;
                S11Reject x = Grabs.Start(key, g.EntityId, r.dealerTechType, nowMs, r.amount, out h);
                if (x != S11Reject.None) { detail = "held by another creature"; return x; }
                Vec3 p;
                Quat q;
                if (AttachSe(r, out p, out q))
                {
                    h.Target = h.PrevTarget = p;
                    h.AttachQ = q;
                    h.PrevMs = nowMs;
                    h.PrevSnTime = r.snTimeSec;
                    h.HasPrev = true;
                    h.RelQ = (q.Conjugate() * QuatOf(g.WorldMatrix)).Normalized();
                    h.HasRel = true;

                    // the grid's centre of bounds, held ON the attach point, as Subnautica holds a Seamoth
 // it moves
 // there over the row's blend (format hold.kinematic). VERIFIED public virtual BoundingBox MyPositionComponentBase.LocalAABB.
 // S11.M13 restores the bite point of 20261024 (the grid point under the mouth, no blend)
                    if (S11SeMutants.BiteAnchor)
                    {
                        h.AnchorLocal = V(Vector3D.Transform(D(p), g.PositionComp.WorldMatrixNormalizedInv));
                        h.BlendMs = 0;
                    }
                    else
                    {
                        Vector3 c = g.PositionComp.LocalAABB.Center;
                        h.AnchorLocal = new Vec3(c.X, c.Y, c.Z);
                        h.BlendMs = row.BlendMs;
                    }
                    h.StartAnchor = V(Vector3D.Transform(D(h.AnchorLocal), g.WorldMatrix));
                    h.HasAnchor = true;
                    //  after the blend the grid lies in the jaw as Subnautica lays a Seamoth
 // its longest box axis on the attach forward, its shortest on the up
                    if (!S11SeMutants.BiteAnchor) { h.RelAlignedQ = (q.Conjugate() * AlignedOrientation(g, q)).Normalized(); h.HasAligned = true; }
                    Surfaced.Remove(key);
                }
                GrabStarts++;
                S15Se.OnCreatureHit(g.EntityId, "s11 grab"); // S15 hook (pick 1): the Cyclops's "hostile organism" line on the aboard grid
                DeLog.Info("DE.S11 grab START id=" + id + " " + row.Creature + " holds grid " + g.EntityId + " (box " + Size(size) + " m" + (fits ? "" : ", S11.M4 ignored the size class") + ", planned " + S11SeText.G(r.amount) + " s)");
                GrabRow("start", id, key, g, row.Creature, size, fits ? null : "S11.M4");
                return S11Reject.None;
            }
            if (!apply) return S11Reject.None;
            S11Held held;
            S11Reject t = Grabs.Touch(key, g.EntityId, nowMs, out held);
            if (t != S11Reject.None)
            {
                // format surface.release: SE let this key go at the surface; Subnautica's next records (before its own rule or its
 // release record arrive) are dropped quietly, not rejected
                ulong at;
                if (Surfaced.TryGetValue(key, out at) && nowMs - at < 30000UL) { SurfacedDrops++; if (phase == S11Conventions.GrabPhase.Release) Surfaced.Remove(key); return S11Reject.None; }
                detail = "no grab of creature key " + key + " on this grid";
                return t;
            }
            if (phase == S11Conventions.GrabPhase.Hold)
            {
                Vec3 p;
                Quat q;
                if (AttachSe(r, out p, out q))
                {
                    // 20261024-030926: the velocity between two records by Subnautica's emit times (arrival times jitter by
 // frames: a 40 m/s segment read up to 49.6 m/s); arrival times without them (an injected record)
                    double snDt = r.snTimeSec - held.PrevSnTime;
                    double dt = held.PrevSnTime > 0 && r.snTimeSec > 0 && snDt > 1e-4 && snDt < 1.0 ? snDt : (nowMs - held.PrevMs) / 1000.0;
                    held.TargetVel = held.HasPrev ? S11GrabRules.TargetVelocity(held.PrevTarget, p, dt) : Vec3.Zero;
                    held.PrevTarget = p;
                    held.PrevMs = nowMs;
                    held.PrevSnTime = r.snTimeSec;
                    held.HasPrev = true;
                    held.Target = p;
                    held.AttachQ = q;
                    // format surface.release: the Water Mod's depth (public float?
 // WaterModClient.Depth, negative below the surface) at the top of the held grid's bounds; within MarginCm lets go
 // (S11.M14: never); the creature's own top is Subnautica's to judge (it has the creature)
                    double depth = TopDepth(g);
                    if (!double.IsNaN(depth) && (double.IsNaN(held.MinMouthDepthM) || depth < held.MinMouthDepthM)) held.MinMouthDepthM = depth;
                    if (S11SurfaceRules.TopReleases(depth) && !S11SeMutants.NoSurface)
                    {
                        Surfaced[key] = nowMs;
                        SurfaceReleases++;
                        Release(held, "surface (the grid's top " + S11SeText.G(depth) + " m deep < " + (S11Conventions.Surface.MarginCm / 100.0).ToString("0.#", CultureInfo.InvariantCulture) + " m)", nowMs, true);
                        return S11Reject.None;
                    }
                }
                held.Holds++;
                GrabHolds++;
                return S11Reject.None;
            }
            Release(held, "release record " + id, nowMs);
            return S11Reject.None;
        }

        /// <summary>The planet's up at a point: against natural gravity (MyGravityProviderSystem.CalculateNaturalGravityInPoint), else radial.</summary>
        private static Vector3D UpAt(Vector3D p)
        {
            Vector3 gv = MyGravityProviderSystem.CalculateNaturalGravityInPoint(p);
            if (gv.LengthSquared() > 1e-6f) return -Vector3D.Normalize((Vector3D)gv);
            MyPlanet planet = SiteBinding.Planet;
            return planet != null ? Vector3D.Normalize(p - planet.PositionComp.GetPosition()) : Vector3D.Up;
        }

        /// <summary>format surface.release: the Water Mod depth at the top of a grid's world bounds along the planet's up.</summary>
        private static double TopDepth(MyCubeGrid g)
        {
            BoundingBoxD b = g.PositionComp.WorldAABB;
            Vector3D c = b.Center, e = b.HalfExtents, up = UpAt(c);
            double sup = Math.Abs(e.X * up.X) + Math.Abs(e.Y * up.Y) + Math.Abs(e.Z * up.Z); // the box's support along up
            return MouthDepth(V(c + up * sup));
        }

        /// <summary>Review 7: the grids of a grid's mechanical group (1 = none attached).</summary>
        private static readonly List<MyCubeGrid> MechScratch = new List<MyCubeGrid>();
        private static int MechanicalCount(MyCubeGrid g)
        {
            try
            {
                MechScratch.Clear();
                MyCubeGridGroups groups = MyCubeGridGroups.Static;
                if (groups == null) return 1;
                groups.Mechanical.GetGroupNodes(g, MechScratch);
                return Math.Max(1, MechScratch.Count);
            }
            catch (Exception) { return 1; }
        }

        /// <summary>
 /// Review 8: the grid's world orientation in the jaw: its longest box axis (LocalAABB size) along the attach forward and
 /// its shortest along the attach up, each sign the one nearest the grid's orientation now, the third axis right-handed.
 /// </summary>
        private static Quat AlignedOrientation(MyCubeGrid g, Quat attach)
        {
            Vector3 s = g.PositionComp.LocalAABB.Size;
            double[] len = { s.X, s.Y, s.Z };
            int L = 2, S = 1;
            for (int i = 0; i < 3; i++) { if (len[i] > len[L] + 1e-6) L = i; }
            S = L == 0 ? 1 : 0;
            for (int i = 0; i < 3; i++) { if (i != L && len[i] < len[S] - 1e-6) S = i; }
            int M = 3 - L - S;
            MatrixD gw = g.WorldMatrix;
            Vector3D[] ax = { gw.Right, gw.Up, gw.Backward };
            MatrixD am = MatrixD.CreateFromQuaternion(new Quaternion((float)attach.X, (float)attach.Y, (float)attach.Z, (float)attach.W));
            Vector3D f = am.Forward, u = am.Up;
            double sl = Vector3D.Dot(ax[L], f) < 0 ? -1 : 1, ss = Vector3D.Dot(ax[S], u) < 0 ? -1 : 1;
            var col = new Vector3D[3];
            col[L] = f * sl;
            col[S] = u * ss;
            col[M] = Vector3D.Cross(col[(M + 1) % 3], col[(M + 2) % 3]);
            MatrixD t = MatrixD.Identity;
            t.Right = col[0];
            t.Up = col[1];
            t.Backward = col[2];
            return QuatOf(t);
        }

        /// <summary>format surface.release: the Water Mod depth at an SE point (positive below the surface), NaN without the API.</summary>
        private static double MouthDepth(Vec3 p)
        {
            WaterModClient api = SiteBinding.Api;
            MyPlanet planet = SiteBinding.Planet;
            if (api == null || planet == null) return double.NaN;
            float? d = api.Depth(D(p), planet);
            return d.HasValue ? S11SurfaceRules.DepthOfWaterModValue(d.Value) : double.NaN;
        }

        private static string Size(Vector3 s) { return s.X.ToString("0.0", CultureInfo.InvariantCulture) + "x" + s.Y.ToString("0.0", CultureInfo.InvariantCulture) + "x" + s.Z.ToString("0.0", CultureInfo.InvariantCulture); }

        private static void GrabRow(string phase, string id, uint key, MyCubeGrid g, string creature, Vector3 size, string why)
        {
            var sb = Head("grab");
            S5Rows.Str(sb, "phase", phase);
            S5Rows.Str(sb, "id", id);
            S5Rows.Int(sb, "key", key);
            S5Rows.Id(sb, "grid", g.EntityId);
            S5Rows.Str(sb, "creature", creature);
            S5Rows.Num(sb, "sx", size.X);
            S5Rows.Num(sb, "sy", size.Y);
            S5Rows.Num(sb, "sz", size.Z);
            S5Rows.Int(sb, "blocks", g.BlocksCount); // pass 6
            S5Rows.Str(sb, "why", why);
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
        }

        private sealed class AfterCheck { public uint Key; public long Grid; public ulong DueMs; public bool Surface; public string Why; public double SpeedAtRelease; }
        private static readonly List<AfterCheck> AfterChecks = new List<AfterCheck>();

        private static void Release(S11Held h, string why, ulong nowMs) { Release(h, why, nowMs, false); }

        /// <summary>

 /// 150 m/s, 6 rad/s) every tick, so a release used to fling the ship. Now the root grid keeps at most ReleaseSpeedCmS,
 /// no angular velocity and, at a surface release, no upward speed
 /// 0.5 s later an s11.grab "after"
 /// row reports its speed and the depth of its top.
 /// </summary>
        private static void Release(S11Held h, string why, ulong nowMs, bool surface)
        {
            Grabs.Remove(h.Key);
            GrabReleases++;
            double v0 = double.NaN;
            try
            {
                MyEntity e;
                var g = MyEntities.TryGetEntityById(h.Grid, out e, false) ? e as MyCubeGrid : null;
                if (g != null && !g.Closed && g.Physics != null && !g.Physics.IsStatic && !S11SeMutants.ForceMode)
                {
                    Vector3 lv = g.Physics.LinearVelocity;
                    v0 = lv.Length();
                    Vector3D up = UpAt(g.Physics.CenterOfMassWorld);
                    Vec3 nv = S11GrabRules.ReleaseVelocity(new Vec3(lv.X, lv.Y, lv.Z), V(up), surface);
                    g.Physics.LinearVelocity = new Vector3((float)nv.X, (float)nv.Y, (float)nv.Z);
                    g.Physics.AngularVelocity = Vector3.Zero;
                    AfterChecks.Add(new AfterCheck { Key = h.Key, Grid = h.Grid, DueMs = nowMs + 500, Surface = surface, Why = why, SpeedAtRelease = v0 });
                }
            }
            catch (Exception) { }
            double heldS = (nowMs - h.StartMs) / 1000.0;
            DeLog.Info("DE.S11 grab RELEASE grid " + h.Grid + " (" + why + ") after " + heldS.ToString("0.0", CultureInfo.InvariantCulture) + " s, " + h.Holds + " hold records, " + h.Forces + " force ticks");
            var sb = Head("grab");
            S5Rows.Str(sb, "phase", "release");
            S5Rows.Int(sb, "key", h.Key);
            S5Rows.Id(sb, "grid", h.Grid);
            S5Rows.Num(sb, "heldS", heldS);
            S5Rows.Num(sb, "plannedS", h.PlannedS);
            S5Rows.Int(sb, "holds", h.Holds);
            S5Rows.Str(sb, "mode", S11SeMutants.ForceMode ? "force" : "kinematic");
            S5Rows.Num(sb, "maxErrorM", h.MaxErrorM);
            S5Rows.Num(sb, "maxTargetSpeed", h.MaxTargetSpeed);
            S5Rows.Int(sb, "snaps", h.Snaps);
            S5Rows.Str(sb, "anchor", S11SeMutants.BiteAnchor ? "bite" : "centre"); //: the jaw centre (S11.C6)
            S5Rows.Num(sb, "blendMs", h.BlendMs);
            S5Rows.Num(sb, "maxJawM", h.MaxJawM);
            S5Rows.Num(sb, "minTopDepthM", h.MinMouthDepthM);
            S5Rows.Num(sb, "speedAtRelease", v0);
            S5Rows.Bool(sb, "surface", surface);
            S5Rows.Str(sb, "why", why);
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
        }

        /// <summary>Every tick: the hold forces of the held grids, the timeouts, the EMP restores (game thread, before the simulation step).</summary>
        public static void Tick(ulong nowMs, bool inWorld)
        {
            if (Enabled && inWorld)
            {
                S11Hud.Ensure(); // pass 5: SE's blood overlay from the last health drop (S11Hud.cs)
                S11Hud.Tick(nowMs);
            }
            if (Enabled && inWorld) S11Volley.Tick(nowMs); //: the scripted volley (de.s11 volley), one missile per step
            if (Enabled && inWorld && AfterChecks.Count > 0) AfterRows(nowMs);
            if (!Enabled || (Grabs.Count == 0 && Emp.Count == 0)) return; //  no allocation per tick when idle
            try
            {
                if (!inWorld)
                {
                    if (Grabs.Count > 0) foreach (uint k in Grabs.Keys().ToArray()) { S11Held h; if (Grabs.TryGet(k, out h)) Release(h, "world gone", nowMs); }
                    if (Emp.Count > 0) EmpRestore(Emp.All(Scratch), "world gone");
                    return;
                }
                foreach (uint k in Grabs.TimedOut(nowMs, S11SeMutants.NoTimeout).ToArray())
                {
                    S11Held h;
                    if (Grabs.TryGet(k, out h)) Release(h, "timeout (no hold record for " + S11Conventions.Hold.TimeoutMs + " ms)", nowMs);
                }
                foreach (S11Held h in new List<S11Held>(Grabs.All)) HoldForce(h, nowMs);
                if (Emp.Count > 0) EmpRestore(Emp.Due(nowMs, Scratch), "time");
            }
            catch (Exception e)
            {
                Errors++;
                if (_errorLog.Allow(nowMs)) DeLog.Info("DE.Error S11 tick " + e.GetType().Name + ": " + e.Message + " " + e.StackTrace);
            }
        }

        /// <summary>Review 1: the released grid 0.5 s on (S11.C8): its speed and the depth of its top (a surface release keeps it under).</summary>
        private static void AfterRows(ulong nowMs)
        {
            for (int i = AfterChecks.Count - 1; i >= 0; i--)
            {
                AfterCheck a = AfterChecks[i];
                if (nowMs < a.DueMs) continue;
                AfterChecks.RemoveAt(i);
                MyEntity e;
                var g = MyEntities.TryGetEntityById(a.Grid, out e, false) ? e as MyCubeGrid : null;
                var sb = Head("grab");
                S5Rows.Str(sb, "phase", "after");
                S5Rows.Int(sb, "key", a.Key);
                S5Rows.Id(sb, "grid", a.Grid);
                S5Rows.Bool(sb, "surface", a.Surface);
                S5Rows.Num(sb, "speedAtRelease", a.SpeedAtRelease);
                S5Rows.Num(sb, "speed", g != null && g.Physics != null ? (double)g.Physics.LinearVelocity.Length() : double.NaN);
                S5Rows.Num(sb, "topDepthM", g != null ? TopDepth(g) : double.NaN);
                S5Rows.Str(sb, "why", a.Why);
                Jsonl.WriteLineBuffered(S5Rows.End(sb));
            }
        }

        private static void HoldForce(S11Held h, ulong nowMs)
        {
            MyEntity e;
            var g = MyEntities.TryGetEntityById(h.Grid, out e, false) ? e as MyCubeGrid : null;
            if (g == null || g.Closed || g.MarkedForClose || g.Physics == null) { Release(h, "grid gone", nowMs); return; }
            if (g.Physics.IsStatic || !g.Physics.Enabled) { Release(h, "grid static", nowMs); return; }
            if (!h.HasPrev) return;
            var phys = g.Physics;
            double m = phys.Mass;
            if (!(m > 0)) return;
            Vector3D com = phys.CenterOfMassWorld;
            // the bite anchor and its velocity;
 // the cap scales with the creature (format hold.force); the force still acts at the centre of mass
            Vector3D anchor = h.HasAnchor ? Vector3D.Transform(D(h.AnchorLocal), g.WorldMatrix) : com;
            if (!S11SeMutants.ForceMode)
            {
                KinematicStep(h, g, phys, com, anchor, nowMs);
                return;
            }
            Vec3 x = V(anchor);
            Vector3 v = h.HasAnchor ? phys.GetVelocityAtPoint(anchor) : phys.LinearVelocity;
            double aMax = S11GrabRules.MaxHoldAccel(Bites.Factor(h.Creature));
            Vec3 a = S11GrabRules.HoldAccel(h.Target, x, h.TargetVel, new Vec3(v.X, v.Y, v.Z), S11Conventions.Hold.OmegaMilli / 1000.0,
                                            S11Conventions.Hold.ZetaPermille / 1000.0, aMax);
            Vector3 grav = MyGravityProviderSystem.CalculateNaturalGravityInPoint(com);
            var f = new Vector3((float)(m * (a.X - grav.X)), (float)(m * (a.Y - grav.Y)), (float)(m * (a.Z - grav.Z)));
            phys.AddForce(MyPhysicsForceType.APPLY_WORLD_FORCE, f, com, null);
            // the Water Mod's buoyancy at its centre of buoyancy, for the share the S7p depth hold does not cancel itself
            WaterModClient api = SiteBinding.Api;
            if (api != null)
            {
                Vector3 bf;
                bool applied;
                Vector3D cb;
                if (api.GridBuoyancyCentreReady && api.TryReadGridBuoyancy(g, out bf, out applied, out cb) && applied)
                {
                    MyEntityThrustComponent thrust;
                    bool holdCancels = DampenerHold.Patched && DampenerHold.ForceMode && g.Components.TryGet(out thrust) && thrust != null && thrust.DampenersEnabled;
                    double share = S11GrabRules.BuoyancyShare(holdCancels, DampenerHold.Factor);
                    if (share > 0) phys.AddForce(MyPhysicsForceType.APPLY_WORLD_FORCE, -bf * (float)share, cb, null);
                }
            }
            // the orientation: the attach orientation × the relative orientation at the start
            if (h.HasRel)
            {
                Quat qt = (h.AttachQ * h.RelQ).Normalized();
                Quat q = QuatOf(g.WorldMatrix);
                Vector3 w = phys.AngularVelocity;
                Vec3 alpha = S11GrabRules.AngAccel(qt, q, new Vec3(w.X, w.Y, w.Z), S11Conventions.Hold.AngOmegaMilli / 1000.0,
                                                   S11Conventions.Hold.ZetaPermille / 1000.0, S11Conventions.Hold.MaxAngAccelMilli / 1000.0);
                Vector3 s = g.PositionComp.LocalAABB.Size;
                double inertia = m * (s.X * s.X + s.Y * s.Y + s.Z * s.Z) / 12.0; // a box's mean moment of inertia
                var tq = new Vector3((float)(alpha.X * inertia), (float)(alpha.Y * inertia), (float)(alpha.Z * inertia));
                //  APPLY_WORLD_FORCE drops the torque (MyPhysicsBody.AddForceInternal → ApplyForceWorld only,/);
 // the impulse path applies a torque as an angular impulse × 1/60 per call (ApplyImplusesWorld)
                phys.AddForce(MyPhysicsForceType.APPLY_WORLD_IMPULSE_AND_WORLD_ANGULAR_IMPULSE, null, null, tq);
                h.LastErrorDeg = S11GrabRules.AngleDeg(qt, q);
            }
            h.Forces++;
            HoldForces++;
            h.LastErrorM = (h.Target - x).Length;
            if (h.LastErrorM > h.MaxErrorM) h.MaxErrorM = h.LastErrorM;
            if (h.TargetVel.Length > h.MaxTargetSpeed) h.MaxTargetSpeed = h.TargetVel.Length;
            if (_holdRow.Allow(nowMs))
            {
                var sb = Head("hold");
                S5Rows.Int(sb, "key", h.Key);
                S5Rows.Id(sb, "grid", h.Grid);
                S5Rows.Num(sb, "errorM", h.LastErrorM);
                S5Rows.Num(sb, "errorDeg", h.HasRel ? h.LastErrorDeg : double.NaN);
                S5Rows.Num(sb, "heldS", (nowMs - h.StartMs) / 1000.0);
                S5Rows.Num(sb, "massKg", m);
                S5Rows.Num(sb, "accel", a.Length);
                S5Rows.Num(sb, "aMax", aMax);
                S5Rows.Str(sb, "mode", "force");
                S5Rows.Num(sb, "maxErrorM", h.MaxErrorM);
                S5Rows.Num(sb, "targetSpeed", h.TargetVel.Length);
                S5Rows.Bool(sb, "anchor", h.HasAnchor);
                S5Rows.Int(sb, "holds", h.Holds);
                Jsonl.WriteLineBuffered(S5Rows.End(sb));
            }
        }

        /// <summary>
 /// S11b 20261024-000956 (format hold.kinematic): the anchor and a 450 m/s2 cap still trailed 27 m (the x3 reaper's
 /// attach point moved at 40 m/s; the 12.5 t wall broke on the cap's forces in 0.8 s). The hold is kinematic: every
 /// tick the error to the mouth (led by its velocity) is measured, over SnapCm the grid's matrix is set (the anchor on
 /// the mouth, the orientation attach × relative-at-start), then the root grid's linear and angular velocities are set
 /// so the anchor moves with the mouth (no force: SE's physics resolves contacts). VERIFIED: <c>public void
 /// MyPositionComponentBase.SetWorldMatrix(ref MatrixD, object, bool, bool, bool, bool skipTeleportCheck,...)</c>
 /// →
 /// <c>MyPhysicsBody.OnWorldPositionChanged</c> sets the rigid body; the settable
 /// <c>LinearVelocity</c> and <c>AngularVelocity</c>; <c>MatrixD.CreateFromQuaternion(Quaternion)</c>.
 /// </summary>
        private static void KinematicStep(S11Held h, MyCubeGrid g, VRage.Game.Components.MyPhysicsComponentBase phys, Vector3D com, Vector3D anchor, ulong nowMs)
        {
            Vec3 xj = S11GrabRules.TargetNow(h.Target, h.TargetVel, nowMs >= h.PrevMs ? (double)(nowMs - h.PrevMs) : 0.0);
            // (format hold.kinematic): the anchor (the grid's centre of bounds) moves from where it was onto the jaw
 // centre over the row's blend, then stays on it
            double heldMs = nowMs >= h.StartMs ? (double)(nowMs - h.StartMs) : 0.0;
            double f = S11GrabRules.BlendFactor(h.BlendMs, heldMs);
            Vec3 xt = S11GrabRules.BlendTarget(h.StartAnchor, xj, f);
            Vec3 vt = S11GrabRules.BlendVelocity(h.StartAnchor, xj, h.TargetVel, f, h.BlendMs);
            // S11.C6: the grid's centre of bounds to the
 // jaw centre now, measured apart from the anchor's own transform; the largest once the blend is over
            Vector3D wc = g.PositionComp.WorldAABB.Center;
            h.LastJawM = (V(wc) - xj).Length;
            if (f >= 1.0 && h.LastJawM > h.MaxJawM) h.MaxJawM = h.LastJawM;
            double errM = (xt - V(anchor)).Length;
            if (errM > h.MaxErrorM) h.MaxErrorM = errM;
            double ts = h.TargetVel.Length;
            if (ts > h.MaxTargetSpeed) h.MaxTargetSpeed = ts;
            Quat q = QuatOf(g.WorldMatrix);
            Quat rel = h.HasAligned ? InstanceMath.Slerp(h.RelQ, h.RelAlignedQ, f) : h.RelQ; //  into Subnautica's attach rotation over the blend
            Quat qt = h.HasRel ? (h.AttachQ * rel).Normalized() : q;
            if (h.HasAnchor && S11GrabRules.Snaps(errM))
            {
                MatrixD mtx = MatrixD.CreateFromQuaternion(new Quaternion((float)qt.X, (float)qt.Y, (float)qt.Z, (float)qt.W));
                mtx.Translation = D(xt) - Vector3D.TransformNormal(D(h.AnchorLocal), mtx);
                g.PositionComp.SetWorldMatrix(ref mtx);
                h.Snaps++;
                HoldSnaps++;
                com = phys.CenterOfMassWorld;
                anchor = D(xt);
                q = qt;
            }
            Vec3 vA = S11GrabRules.ServoVelocity(xt, V(anchor), vt);
            Vec3 w = h.HasRel ? S11GrabRules.ServoAngular(qt, q) : Vec3.Zero;
            Vector3D wD = D(w);
            Vector3D vCom = D(vA) - Vector3D.Cross(wD, anchor - com);
            phys.LinearVelocity = new Vector3((float)vCom.X, (float)vCom.Y, (float)vCom.Z);
            phys.AngularVelocity = new Vector3((float)w.X, (float)w.Y, (float)w.Z);
            h.LastErrorM = errM;
            h.LastErrorDeg = h.HasRel ? S11GrabRules.AngleDeg(qt, QuatOf(g.WorldMatrix)) : double.NaN;
            h.Forces++;
            HoldForces++;
            if (_holdRow.Allow(nowMs))
            {
                var sb = Head("hold");
                S5Rows.Int(sb, "key", h.Key);
                S5Rows.Id(sb, "grid", h.Grid);
                S5Rows.Num(sb, "errorM", h.LastErrorM);
                S5Rows.Num(sb, "errorDeg", h.LastErrorDeg);
                S5Rows.Num(sb, "heldS", (nowMs - h.StartMs) / 1000.0);
                S5Rows.Num(sb, "massKg", phys.Mass);
                S5Rows.Str(sb, "mode", "kinematic");
                S5Rows.Num(sb, "targetSpeed", ts);
                S5Rows.Int(sb, "snaps", h.Snaps);
                S5Rows.Num(sb, "maxErrorM", h.MaxErrorM);
                S5Rows.Bool(sb, "anchor", h.HasAnchor);
                S5Rows.Int(sb, "holds", h.Holds);
                S5Rows.Num(sb, "jawM", h.LastJawM); //: the centre of bounds to the jaw centre (S11.C6)
                S5Rows.Num(sb, "blend", f);
                S5Rows.Num(sb, "mouthDepthM", h.MinMouthDepthM);
                Jsonl.WriteLineBuffered(S5Rows.End(sb));
            }
        }

        // =============================================================================================: missiles

        private static OriginGetter _originRef;
        private static System.Reflection.MethodInfo _explode;
        private static bool _missileReflected;

        private delegate long OriginGetter(MyMissile m);

        /// <summary>
 /// format missile.
 /// creature; SE moves the missile to that point and explodes it there, so its own explosion (and its Explosion row) happens
 /// where the creature is. VERIFIED: <c>public bool HasHealthPool</c>, <c>public float
 /// HealthPool</c>, <c>public MyExplosionTypeEnum ExplosionType</c>, <c>public bool ShouldExplode</c>,
 /// <c>private void Explode</c> (m_shouldExplode, the launcher's RemoveMissile), <c>private long m_originEntity</c>
 /// (ExecuteExplosion's OriginEntity), <c>public void MyPositionComponentBase.SetPosition(Vector3D, object, bool, bool)</c>
 /// A health-pool slug (railgun, artillery: MissileExplosionDamage 0, Ammos.sbc) adds one
 /// synthetic Explosion row of HealthPool x SlugPermille (no SE damage: S11Blasts.AddSynthetic). Game thread.
 /// </summary>
        private static S11Reject MissileHit(DamageRecord r, string id, bool apply, ulong nowMs, out string detail)
        {
            detail = null;
            long mid = (long)r.targetEntityId;
            MyEntity e;
            var m = MyEntities.TryGetEntityById(mid, out e, false) ? e as MyMissile : null;
            if (m == null || m.Closed || m.MarkedForClose || m.ShouldExplode)
            {
                MissileGone++;
                detail = "missile " + mid + " gone (it exploded elsewhere first or its range ended)";
                MissileRow("gone", id, mid, r, double.NaN, false, 0, 0, detail);
                return S11Reject.MissileGone;
            }
            SiteMap map = SiteBinding.Map;
            if (map == null) { detail = "no site map"; return S11Reject.NotApplied; }
            Vec3 p = map.SnToSe(new Vec3(r.snWorldHit.x, r.snWorldHit.y, r.snWorldHit.z));
            double lag = (V(m.PositionComp.GetPosition()) - p).Length;
            //  a back-cast hit behind the launch point is refused
            Vector3 fl = m.LinearVelocity;
            if (S11MissileRules.Behind(p, V(m.Origin), new Vec3(fl.X, fl.Y, fl.Z)))
            {
                detail = "the hit lies behind the launch point";
                MissileRow("behind", id, mid, r, lag, false, 0, 0, detail);
                return S11Reject.Behind;
            }
            if (!apply) { detail = "would explode " + S11SeText.G(lag) + " m back"; return S11Reject.None; }
            EnsureMissileReflection();
            bool slug = m.HasHealthPool;
            double pool = m.HealthPool;
            long origin = _originRef != null ? _originRef(m) : 0L;
            int cls = S11Blasts.ClassOfOriginId(origin);
            bool holder = r.snDamageType == S11Conventions.Missile.HitHolder;
            double yield = 0;
            if (holder)
            {
                //  the missile left the grid the creature holds; the hit point lies at the jaw, inside that grid: it explodes
 // where it is with no damage (the player's hull untouched) and the creature alone takes its damage, a synthetic row
 // at the hit point
                var ammo = m.AmmoDefinition as Sandbox.Definitions.MyMissileAmmoDefinition;
                yield = slug ? S11MissileRules.SlugYield(pool) : m.ExplosionDamage;
                double radius = Math.Max(1.0, ammo != null ? ammo.MissileExplosionRadius : 4.0);
                if (yield > 0) S11Blasts.AddSynthetic(D(p), 1, slug ? S11Conventions.Missile.SlugRadiusCm / 100.0 : radius, yield, 0, cls);
                m.ExplosionDamage = 0f;
                if (slug) m.HealthPool = 0f;
            }
            else
            {
                m.PositionComp.SetPosition(D(p));
                if (slug)
                {
                    yield = S11MissileRules.SlugYield(pool);
                    if (yield > 0) { S11Blasts.AddSynthetic(D(p), 1, S11Conventions.Missile.SlugRadiusCm / 100.0, yield, 0, cls); MissileSlugs++; }
                    m.HealthPool = 0f;
                }
            }
            m.ExplosionType = MyExplosionTypeEnum.MISSILE_EXPLOSION;
            if (_explode != null) _explode.Invoke(m, null);
            else m.ShouldExplode = true;
            MissileHits++;
            MissileRow(holder ? "detonateHolder" : "detonate", id, mid, r, lag, slug, pool, yield, S11Conventions.WeaponClass.Name(cls));
            return S11Reject.None;
        }

        private static void EnsureMissileReflection()
        {
            if (_missileReflected) return;
            _missileReflected = true;
            try
            {
                System.Reflection.FieldInfo fi = HarmonyLib.AccessTools.Field(typeof(MyMissile), "m_originEntity");
                if (fi != null && fi.FieldType == typeof(long)) { var fr = HarmonyLib.AccessTools.FieldRefAccess<MyMissile, long>(fi); _originRef = mm => fr(mm); }
                else DeLog.Info("DE.S11 MISSING MyMissile.m_originEntity (Int64) (a slug's synthetic row takes the Explosion class)");
                System.Reflection.MethodInfo mi = HarmonyLib.AccessTools.Method(typeof(MyMissile), "Explode", Type.EmptyTypes);
                if (mi != null && mi.DeclaringType == typeof(MyMissile) && mi.ReturnType == typeof(void) && !mi.IsStatic) _explode = mi;
                else DeLog.Info("DE.S11 MISSING MyMissile.Explode() (the missile explodes through ShouldExplode; its launcher keeps it listed)");
            }
            catch (Exception ex) { DeLog.Info("DE.S11 MISSING MyMissile members: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static void MissileRow(string phase, string id, long missile, DamageRecord r, double lag, bool slug, double pool, double yield, string why)
        {
            var sb = Head("missile");
            S5Rows.Str(sb, "phase", phase);
            S5Rows.Str(sb, "id", id);
            S5Rows.Id(sb, "missile", missile);
            S5Rows.Int(sb, "creature", r.dealerTechType);
            S5Rows.Int(sb, "instance", r.dealerInstanceId);
            S5Rows.Num(sb, "lagM", lag);
            S5Rows.Bool(sb, "slug", slug);
            S5Rows.Num(sb, "pool", pool);
            S5Rows.Num(sb, "yield", yield);
            S5Rows.Str(sb, "why", why);
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
        }

        // ============================================================================================= C: pushes

        private static S11Reject Push(DamageRecord r, string id, bool apply, out string detail)
        {
            detail = null;
            S11Reject why;
            MyCubeGrid g = GridOf(r, out why);
            if (g == null) { detail = "grid not mirrored"; return why; }
            S11PushRow row;
            if (!S11Conventions.TryPushRow(r.dealerTechType, out row)) { detail = "creature " + r.dealerTechType + " has no push row"; return S11Reject.NotGrabbable; }
            if (g.Physics == null || g.Physics.IsStatic || g.IsStatic) { detail = "static grid"; return S11Reject.Static; }
            // pass 6: a remnant is never pushed (Subnautica pushes none; SE checks with the exact block count)
            Vector3 box = g.PositionComp.LocalAABB.Size;
            if (S11DebrisRules.IsDebris(g.BlocksCount, (double)box.X * box.Y * box.Z) && !S11SeMutants.NoDebrisFilter)
            { detail = "debris: " + g.BlocksCount + " block(s)"; return S11Reject.Debris; }
            SiteMap map = SiteBinding.Map;
            if (map == null) { detail = "no site map"; return S11Reject.NotApplied; }
            double m = g.Physics.Mass;
            double dv = S11PushRules.Dv(row, r.amount, m);
            if (!apply) { detail = "would push " + S11SeText.G(dv) + " m/s"; return S11Reject.None; }
            Vec3 p = map.SnToSe(new Vec3(r.snWorldHit.x, r.snWorldHit.y, r.snWorldHit.z));
            Vec3 d = map.DirSnToSe(p, new Vec3(r.attackDirSn.x, r.attackDirSn.y, r.attackDirSn.z)).Normalized();
            double j = dv * m;
            var imp = new Vector3((float)(d.X * j), (float)(d.Y * j), (float)(d.Z * j));
            g.Physics.AddForce(MyPhysicsForceType.APPLY_WORLD_IMPULSE_AND_WORLD_ANGULAR_IMPULSE, imp, D(p), null);
            Pushes++;
            var sb = Head("push");
            S5Rows.Str(sb, "id", id);
            S5Rows.Id(sb, "grid", g.EntityId);
            S5Rows.Num(sb, "impulse", r.amount);
            S5Rows.Num(sb, "massKg", m);
            S5Rows.Num(sb, "dv", dv);
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
            return S11Reject.None;
        }

        // ============================================================================================= lifecycle and status

 /// <summary>World unload (MySession.OnUnloading): every block S11 switched off is switched on again, every grab released.</summary>
 /// <summary>The save postfix: true when this block is switched off by an S11 EMP right now.</summary>
        internal static bool EmpOff(long blockId) { return Emp.IsOurs(blockId); }

        /// <summary>
 /// S6's grid hits (unit 1, Normal) by a creature of the scale table are multiplied by its BiteMult (a 2.5 x reaper bites a
 /// grid for 2.5 x 80 SN_HP before k); every other record, the character's and S11 off: 1.
 /// </summary>
        public static double GridBiteMultiplier(DamageRecord r)
        {
            if (!Enabled || r.unit != DamageUnit.SnHp || r.snDamageType != CombatConventions.SnDamageForward.Normal || r.dealerTechType == 0) return 1.0;
            double m = Bites.BiteMult(r.dealerTechType);
            if (m != 1.0) BiteMultiplied++;
            return m;
        }

        public static void OnUnload()
        {
            try
            {
                if (Emp.Count > 0) EmpRestore(Emp.All(Scratch), "world unload");
                S11Guard.Set(false, "world unload");
                Grabs.Clear();
                Caps.Clear();
                Surfaced.Clear();
                S11Volley.Clear();
            }
            catch (Exception) { }
        }

        public static void ReleaseAll(string why)
        {
            ulong now = Wire.Native.Win32.TickCount64();
            foreach (uint k in Grabs.Keys().ToArray()) { S11Held h; if (Grabs.TryGet(k, out h)) Release(h, why, now); }
        }

        public static Dictionary<string, object> Status()
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            d["enabled"] = Enabled;
            d["drained"] = Drained;
            d["applied"] = Applied;
            d["hazards"] = AppliedHazard;
            d["hazardSe"] = HazardSe;
            d["heals"] = AppliedHeal;
            d["healSe"] = HealSe;
            d["oxygen"] = AppliedOxygen;
            d["oxygenL"] = OxygenL;
            d["emp"] = AppliedEmp;
            d["empBlocksOff"] = Emp.Count;
            d["grabStarts"] = GrabStarts;
            d["grabHolds"] = GrabHolds;
            d["grabReleases"] = GrabReleases;
            d["surfaceReleases"] = SurfaceReleases;
            d["surfacedDrops"] = SurfacedDrops;
            d["missileHits"] = MissileHits;
            d["missileGone"] = MissileGone;
            d["missileSlugs"] = MissileSlugs;
            d["volley"] = S11Volley.Describe();
            d["held"] = Grabs.Count;
            d["holdForces"] = HoldForces;
            d["pushes"] = Pushes;
            d["capped"] = Caps.Capped;
            d["errors"] = Errors;
            d["mutants"] = S11SeMutants.Active;
            d["bites"] = Bites.Describe();
            d["biteMultiplied"] = BiteMultiplied;
            var rj = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 1; i < Rejected.Length; i++) if (Rejected[i] > 0) rj[((S11Reject)i).ToString()] = Rejected[i];
            d["rejected"] = rj;
            d["guard"] = S11Guard.On;
            d["hud"] = S11Hud.Describe();
            d["character"] = S11Guard.CharacterState();
            return d;
        }
    }

    /// <summary>
 /// Pass 4: the harness guard, <c>de.s11 {guard: true|false}</c> (dev builds only, never set by
 /// a play session). Two scripted reapers bit the SE character to death in the danger part (80 + 80 of its 100); SE then
 /// opened its respawn screen, which pauses a single-player session ("Updating stopped.", the tick frozen), and every later
 /// part judged a paused world. The guard is SE's own admin invulnerability on the local player: <c>MyCharacter.DoDamage</c>
 /// returns false when <c>MySession.Static.RemoteAdminSettings[steamId]</c> has <c>AdminSettingsEnum.Invulnerable</c> for
 /// the identity of serial 0
 /// The launcher sets it after the hazard and
 /// air parts (they need real damage and healing) and clears it at teardown; the previous entry is restored exactly (and on
 /// world unload). Bites on a guarded character read "returned false" in S6's DE.Damage lines.
 /// </summary>
    internal static class S11Guard
    {
        private static bool _on, _hadKey, _hadFlag;
        private static ulong _steam;

        public static bool On { get { return _on; } }

        public static string Set(bool on, string via)
        {
            try
            {
                Dictionary<ulong, AdminSettingsEnum> admin = MySession.Static != null ? MySession.Static.RemoteAdminSettings : null;
                if (!on)
                {
                    if (!_on) return "guard already off";
                    _on = false;
                    if (admin != null)
                    {
                        AdminSettingsEnum v;
                        if (!_hadKey) admin.Remove(_steam);
                        else if (!_hadFlag && admin.TryGetValue(_steam, out v)) admin[_steam] = v & ~AdminSettingsEnum.Invulnerable;
                    }
                    string off = "DE.S11 guard OFF (" + via + "): the character takes damage again (admin settings of " + _steam + " restored)";
                    DeLog.Info(off);
                    return off;
                }
                if (_on) return "guard already on";
                if (admin == null) return "no session";
                MyCharacter c = Current();
                if (c == null) return "no character";
                MyPlayer.PlayerId pid = c.GetClientIdentity();
                if (pid.SerialId != 0) return "the character's identity has serial " + pid.SerialId + " (DoDamage's admin test needs 0)";
                AdminSettingsEnum cur;
                _steam = pid.SteamId;
                _hadKey = admin.TryGetValue(_steam, out cur);
                _hadFlag = _hadKey && (cur & AdminSettingsEnum.Invulnerable) != 0;
                admin[_steam] = cur | AdminSettingsEnum.Invulnerable;
                _on = true;
                string line = "DE.S11 guard ON (" + via + "): admin invulnerability on " + _steam + " so scripted leviathans cannot kill the harness character (SE's respawn screen pauses the session)";
                DeLog.Info(line);
                return line;
            }
            catch (Exception e)
            {
                return "guard failed: " + e.GetType().Name + ": " + e.Message;
            }
        }

        public static Dictionary<string, object> CharacterState()
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                MyCharacter c = Current();
                d["present"] = c != null;
                if (c == null) return d;
                d["alive"] = !c.IsDead;
                MyEntityStat h = c.StatComp != null ? c.StatComp.Health : null;
                if (h != null) d["health"] = (double)h.Value;
            }
            catch (Exception e) { d["error"] = e.GetType().Name; }
            return d;
        }

        private static MyCharacter Current()
        {
            long id = CharacterExport.CurrentCharacterId;
            MyEntity e;
            return id != 0 && MyEntities.TryGetEntityById(id, out e, false) ? e as MyCharacter : null;
        }
    }

    /// <summary>
 /// Review 1: an EMP switches producers off through <c>MyFunctionalBlock.Enabled</c>, a saved property, and SE
 /// saves before it unloads (MySession.Save before Unload): a save inside the power-loss window would store them off and the
 /// in-memory restore list is gone after a reload. A postfix on <c>public override MyObjectBuilder_CubeBlock
 /// MyFunctionalBlock.GetObjectBuilderCubeBlock(bool copy)</c>
 /// writes
 /// <c>Enabled = true</c> into the build pass of a block S11 switched off (<see cref="S11EmpRules"/>' window), as the jetpack
 /// rule keeps the saved state the player's own (CONTRACT §1). Installed at the first EMP; a miss logs
 /// <c>DE.S11 MISSING MyFunctionalBlock.GetObjectBuilderCubeBlock(Boolean)</c> once and the EMP still works (unsaved risk).
 /// </summary>
    internal static class S11SavePatch
    {
        private const string Id = "deepengineers.se.s11.save";
        private static bool _tried;
        public static bool Patched { get; private set; }
        public static long Rewritten;

        public static void Ensure()
        {
            if (_tried) return;
            _tried = true;
            try
            {
                System.Reflection.MethodInfo m = HarmonyLib.AccessTools.Method(typeof(MyFunctionalBlock), "GetObjectBuilderCubeBlock", new[] { typeof(bool) });
                if (m == null || m.DeclaringType != typeof(MyFunctionalBlock) || m.ReturnType != typeof(VRage.Game.MyObjectBuilder_CubeBlock))
                {
                    DeLog.Info("DE.S11 MISSING MyFunctionalBlock.GetObjectBuilderCubeBlock(Boolean) (an EMP-switched producer may be saved off)");
                    return;
                }
                new HarmonyLib.Harmony(Id).Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(S11SavePatch).GetMethod(nameof(Postfix),
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
                Patched = true;
                DeLog.Info("DE.Harmony SE s11 save: MyFunctionalBlock.GetObjectBuilderCubeBlock postfix (an EMP-switched power producer is saved on)");
            }
            catch (Exception e)
            {
                DeLog.Info("DE.S11 MISSING MyFunctionalBlock.GetObjectBuilderCubeBlock(Boolean): " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void Postfix(MyFunctionalBlock __instance, VRage.Game.MyObjectBuilder_CubeBlock __result)
        {
            try
            {
                var fb = __result as VRage.Game.MyObjectBuilder_FunctionalBlock;
                if (fb == null || __instance == null) return;
                bool saved = S11EmpBook.SavedEnabled(S11Consumer.EmpOff(__instance.EntityId), fb.Enabled);
                if (saved != fb.Enabled) { fb.Enabled = saved; Rewritten++; }
            }
            catch (Exception) { }
        }
    }
}
