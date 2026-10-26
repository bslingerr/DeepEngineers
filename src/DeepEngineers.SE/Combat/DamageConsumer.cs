using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;

namespace DeepEngineers.SE.Combat
{
    /// <summary>
 /// The S6 damage consumer (ROADMAP S6 "SE plugin"; CONTRACT §4.1 "Application rules", §5.4 C8): drains C8 on SE's
 /// update thread right after the mirror producer (≤ 1,024 records per tick, only while the link is Up and the hello is
 /// acknowledged), checks every record in the contract's order (<see cref="DamagePipeline"/>: generation, epoch, the
 /// 8,192-id dedup window per Subnautica generation, unit/amount/kind/type, entity/section/frame), then applies it:
 /// a grid hit point (SE-local of the record's target, a subgrid's own frame when flagged) selects the cell
 /// <c>round(l / GridSize)</c>, else the nearest occupied cell of its 26 neighbours, and that block takes
 /// <c>MySlimBlock.DoDamage(amount × k, DeepBite|DeepBlast|DeepShock, sync: true, hitInfo, attackerId: 0)</c>, the applied
 /// amount read as the drop of <c>Integrity − AccumulatedDamage</c>; a character hit takes
 /// <c>MyCharacter.DoDamage(amount × k_char, type, updateSync: true, attackerId: 0)</c>, the applied amount read as the drop of
 /// <c>StatComp.Health.Value</c>. Gates (Survival with destructible blocks; S6.M4 proves the character one): world
 /// <c>DestructibleBlocks</c> and <c>grid.BlocksDestructionEnabled || block.ForceBlockDestructible</c>, else
 /// <c>NotDestructible</c>; <c>MyCharacter.CharactersCanDie</c> and <c>CharacterCanDie</c>, else <c>CannotDie</c> (one
 /// <c>DE.Damage INACTIVE …</c> line each per world). One <c>DE.Damage applied …</c> line and one s6.damage row per applied
 /// record; rejections counted per reason (s6.stats) and rowed (s6.reject, the first 200 per reason per minute).
 /// <para>Game members: <c>public bool MySlimBlock.DoDamage(float damage,
 /// MyStringHash damageType, bool sync, MyHitInfo? hitInfo, long attackerId, long realHitEntityId = 0L, bool
 /// shouldDetonateAmmo = true, MyStringHash? extraInfo = null)</c>; <c>MySlimBlock.Integrity</c>,
 /// <c>AccumulatedDamage</c>, <c>ForceBlockDestructible</c>, <c>BlockDefinition</c>, <c>ComputeWorldCenter(out
 /// Vector3D)</c>; <c>MyCubeGrid.GetCubeBlock(Vector3I)</c>, <c>GridSize</c>, <c>BlocksDestructionEnabled</c>,
 /// <c>PositionComp.WorldMatrixRef</c>; <c>MySession.Static.Settings.DestructibleBlocks</c>, <c>CreativeMode</c>;
 /// <c>public bool MyCharacter.DoDamage(float damage, MyStringHash damageType, bool updateSync, long attackerId = 0L,
 /// MyStringHash? extraInfo = null)</c>, <c>static MyCharacter.CharactersCanDie</c>, <c>CharacterCanDie</c>,
 /// <c>StatComp.Health.Value</c>; <c>MyStringHash.GetOrCompute(string)</c>; <c>MyHitInfo.Position</c> (Vector3D),
 /// <c>Normal</c> (Vector3); <c>MyEntities.TryGetEntityById(long, out MyEntity, bool = false)</c>.</para>
 /// </summary>
    internal static class DamageConsumer
    {
        private static readonly DedupWindow Dedup = new DedupWindow(CombatConventions.Limits.DedupWindow);
        private static readonly long[] Rejected = new long[32];
        private static readonly RejectRowLimiter RejectRows = new RejectRowLimiter();
        private static bool _genKnown;
        private static uint _epoch;
        private static uint _injectSeq;
        private static string _blocksInactive, _characterInactive;
        private static RateLimit _errorLog = new RateLimit(10000);
        private static RateLimit _mismatchLog = new RateLimit(10000);
        private static ulong _nextStatsMs;
        private static string _lastStatsKey;

        public static long Drained, Applied, AppliedGrid, AppliedCharacter, ScaleMismatch, Injected, InjectedApplied, Errors, GenerationResets;
        public static double AppliedSeTotal;

        // ============================================================================================= per tick

 /// <summary>From <c>Plugin.UpdateCore</c> right after <c>GridMirrorProducer.Tick</c> (update thread).</summary>
        public static void Tick(ulong nowMs, Link link, uint worldEpoch, bool inWorld)
        {
            if (!S6Settings.Enabled) return;
            try { TickCore(nowMs, link, worldEpoch, inWorld); }
            catch (Exception e)
            {
                Errors++;
                if (_errorLog.Allow(nowMs)) DeLog.Info("DE.Error S6 damage #" + Errors + " " + e.GetType().Name + ": " + e.Message + " " + e.StackTrace);
            }
        }

        private static void TickCore(ulong nowMs, Link link, uint worldEpoch, bool inWorld)
        {
            if (worldEpoch != _epoch)
            {
                _epoch = worldEpoch;
                _blocksInactive = _characterInactive = null; // the INACTIVE lines are logged once per world
            }
            bool linked = link != null && link.State == LinkState.Up && inWorld && GridMirrorProducer.StreamingNow;
            if (linked)
            {
                uint gen = link.LastSnGeneration;
                if (!_genKnown || gen != Dedup.Generation)
                {
                    Dedup.Reset(gen);
                    _genKnown = true;
                    GenerationResets++;
                    DeLog.Info("DE.S6 damage: dedup window reset for Subnautica generation " + gen + " (CONTRACT §4.1: the C8 tail re-syncs with the link)");
                }
                Dedup.Bypass = S6Mutants.DedupOff;
                for (int n = 0; n < S6Settings.MaxRecordsPerTick; n++)
                {
                    DamageRecord r;
                    if (!link.TryReadDamage(out r)) break;
                    Drained++;
                    Dictionary<string, object> ignored;
                    // review F8: one bad record never abandons the rest of the tick's drain
                    try { Process(r, gen, worldEpoch, true, false, nowMs, out ignored); }
                    catch (Exception e)
                    {
                        Errors++;
                        try { Reject(r, DamageReject.HitPoint, "exception " + e.GetType().Name + ": " + e.Message, false, nowMs, new Dictionary<string, object>(StringComparer.Ordinal)); }
                        catch (Exception) { }
                    }
                }
            }
            StatsRow(nowMs, link);
        }

        // ============================================================================================= one record

 /// <summary>Checks and (when <paramref name="apply"/>) applies one record. Returns the reject reason (None = applied or would apply).</summary>
        private static DamageReject Process(DamageRecord r, uint snGeneration, uint worldEpoch, bool apply, bool injected, ulong nowMs, out Dictionary<string, object> result)
        {
            result = injected ? new Dictionary<string, object>(StringComparer.Ordinal) : null;
            // S11 hook: units 17-22 (CONTRACT §5.4 "S11 conventions") go to the S11 consumer with this dedup window (exactly once across S6 and S11)
            if (S11Conventions.Unit.IsS11(r.unit))
            {
                DamageReject s11 = S11Consumer.Process(r, snGeneration, worldEpoch, apply ? Dedup : null, apply, injected, nowMs, result);
                return s11 == DamageReject.None ? s11 : Reject(r, s11, "S11 record header/dedup", injected, nowMs, result);
            }
            long characterId = CharacterExport.CurrentCharacterId;
            DamageVerdict v = DamagePipeline.Check(r, snGeneration, worldEpoch, apply ? Dedup : null, SkinOf, characterId, S6Mutants.SwapGridFactors);
            if (result != null)
            {
                result["id"] = DamageRules.HitId(r.snGeneration, r.dedupSeq);
                result["kind"] = S6Rows.KindName(r.targetKind);
                result["target"] = ((long)r.targetEntityId).ToString(CultureInfo.InvariantCulture);
                result["section"] = r.sectionId;
                result["amount"] = (double)r.amount;
                result["type"] = (int)r.snDamageType;
            }
            if (v.Reject == DamageReject.None && v.ScaleMismatch)
            {
                ScaleMismatch++;
                if (_mismatchLog.Allow(nowMs))
                    DeLog.Info("DE.Damage scale mismatch id=" + DamageRules.HitId(r.snGeneration, r.dedupSeq) + " seScaleExpected=" + S3Rows.G(r.seScaleExpected) +
                               " SE k=" + S3Rows.G(v.K) + " (SE uses its own factor)");
            }
            if (v.Reject != DamageReject.None) return Reject(r, v.Reject, v.Detail, injected, nowMs, result);

            var a = new S6Rows.Applied
            {
                R = r,
                K = v.K,
                KExpected = r.seScaleExpected,
                Se = r.amount * v.K,
                SeType = CombatGate.SeType(r.snDamageType),
                Tick = GridMirrorProducerTick(),
                Injected = injected,
            };
            if (!v.Character) a.Se *= S11Consumer.GridBiteMultiplier(r);
            string detail;
            DamageReject g = v.Character ? ApplyCharacter(r, a, apply, out detail) : ApplyGrid(r, a, apply, out detail);
            if (g != DamageReject.None) return Reject(r, g, detail, injected, nowMs, result);
            if (result != null)
            {
                result["outcome"] = apply ? "applied" : "would apply";
                result["k"] = a.K;
                result["se"] = a.Se;
                result["seType"] = a.SeType;
                result["applied"] = apply ? (object)a.AppliedAmount : null;
                if (a.Block.HasValue) result["block"] = new object[] { a.Block.Value.x, a.Block.Value.y, a.Block.Value.z };
                result["blockDef"] = a.BlockDef;
                result["searched"] = a.Searched;
                if (!double.IsNaN(a.EffBefore)) { result["effBefore"] = a.EffBefore; result["effAfter"] = a.EffAfter; }
                if (!double.IsNaN(a.HpBefore)) { result["hpBefore"] = a.HpBefore; result["hpAfter"] = a.HpAfter; }
                if (v.ScaleMismatch) result["scaleMismatch"] = true;
            }
            if (!apply) return DamageReject.None;
            Applied++;
            if (v.Character) AppliedCharacter++; else AppliedGrid++;
            if (!v.Character) S15Se.OnCreatureHit((long)r.targetEntityId, "s6 bite"); // S15 hook (pick 1): the Cyclops's "hostile organism" line on the aboard grid
            if (injected) InjectedApplied++;
            AppliedSeTotal += a.AppliedAmount;
            DeLog.Info(S6Rows.AppliedLine(a));
            Jsonl.WriteLineBuffered(S6Rows.Damage(Utc(), a));
            return DamageReject.None;
        }

        private static DamageReject Reject(DamageRecord r, DamageReject why, string detail, bool injected, ulong nowMs, Dictionary<string, object> result)
        {
            int i = (int)why;
            if (i >= 0 && i < Rejected.Length) Rejected[i]++;
            if (RejectRows.Allow(why, nowMs)) Jsonl.WriteLineBuffered(S6Rows.Reject(Utc(), r, why, detail, injected));
            if (result != null)
            {
                result["outcome"] = "rejected";
                result["reason"] = why.ToString();
                result["detail"] = detail;
            }
            return why;
        }

        private static SentSkin SkinOf(long entityId)
        {
            SentSkin s;
            return CollisionSkin.TryGetSkin(entityId, out s) ? s : null;
        }

        private static ulong GridMirrorProducerTick()
        {
            MySession s = MySession.Static;
            return s != null ? (ulong)(uint)s.GameplayFrameCounter : 0;
        }

        // ============================================================================================= grid

        private static DamageReject ApplyGrid(DamageRecord r, S6Rows.Applied a, bool apply, out string detail)
        {
            detail = null;
            MyCubeGrid grid;
            SentSkin skin;
            if (!CollisionSkin.TryGetGrid((long)r.targetEntityId, out grid, out skin)) { detail = "grid closed"; return DamageReject.NoBlock; }
            Vec3 l = DamagePipeline.Local(r);
            double g = grid.GridSize;
            I32x3 cell;
            bool searched;
            MyCubeGrid gr = grid;
            if (!DamageRules.NearestOccupied(l, g, c => gr.GetCubeBlock(new Vector3I(c.x, c.y, c.z)) != null, out cell, out searched))
            {
                I32x3 own = DamageRules.HitCell(l, g);
                detail = "no block at cell " + own.x + "," + own.y + "," + own.z + " nor its 26 neighbours";
                return DamageReject.NoBlock;
            }
            a.Block = cell;
            a.Searched = searched;
            // review F2: an independent cell from Subnautica's world hit through the site map and the live grid matrix, so
 // the "block under the hit point" checks (N8, B-T1e) do not compare a value with itself
            try
            {
                SiteMap map = SiteBinding.Map;
                if (map != null)
                {
                    Vec3 pSe = map.SnToSe(new Vec3(r.snWorldHit.x, r.snWorldHit.y, r.snWorldHit.z));
                    Vector3D lw = Vector3D.Transform(new Vector3D(pSe.X, pSe.Y, pSe.Z), grid.PositionComp.WorldMatrixNormalizedInv);
                    I32x3 c2 = DamageRules.HitCell(new Vec3(lw.X, lw.Y, lw.Z), g);
                    a.WorldCellDelta = Math.Max(Math.Abs(c2.x - cell.x), Math.Max(Math.Abs(c2.y - cell.y), Math.Abs(c2.z - cell.z)));
                }
            }
            catch (Exception) { a.WorldCellDelta = -1; }
            MySlimBlock blk = grid.GetCubeBlock(new Vector3I(cell.x, cell.y, cell.z));
            if (blk == null) { detail = "block vanished"; return DamageReject.NoBlock; }
            a.BlockDef = blk.BlockDefinition != null ? blk.BlockDefinition.Id.ToString() : null;
            MySession session = MySession.Static;
            bool world = session != null && session.Settings != null && session.Settings.DestructibleBlocks;
            string reason;
            if (!CombatGate.Blocks(world, grid.BlocksDestructionEnabled, blk.ForceBlockDestructible, out reason))
            {
                if (_blocksInactive != reason)
                {
                    _blocksInactive = reason;
                    DeLog.Info("DE.Damage INACTIVE blocks: " + reason + " (records rejected NotDestructible; the S6 SE world must be Survival with destructible blocks)");
                }
                detail = reason;
                return DamageReject.NotDestructible;
            }
            if (a.SeType == null) { detail = "type " + r.snDamageType + " not forwarded"; return DamageReject.Type; }
            double before = blk.Integrity - blk.AccumulatedDamage;
            a.EffBefore = before;
            if (!apply)
            {
                a.EffAfter = before;
                return DamageReject.None;
            }
            MatrixD wm = grid.PositionComp.WorldMatrixRef;
            Vector3D pw = Vector3D.Transform(new Vector3D(l.X, l.Y, l.Z), ref wm);
            Vector3D centre;
            blk.ComputeWorldCenter(out centre);
            Vector3D nd = pw - centre;
            Vector3 n = nd.LengthSquared() > 1e-12 ? (Vector3)Vector3D.Normalize(nd) : (Vector3)wm.Up;
            var hit = new MyHitInfo { Position = pw, Normal = n };
            blk.DoDamage((float)a.Se, MyStringHash.GetOrCompute(a.SeType), true, hit, 0L);
            double after = blk.Integrity - blk.AccumulatedDamage;
            a.EffAfter = after;
            a.AppliedAmount = before - after;
            if (!(a.AppliedAmount > 0))
            {
                detail = "Integrity - AccumulatedDamage unchanged (" + S3Rows.G(before) + "): a safe zone or a fat block consumed it";
                return DamageReject.NotApplied;
            }
            return DamageReject.None;
        }

        // ============================================================================================= character

        private static DamageReject ApplyCharacter(DamageRecord r, S6Rows.Applied a, bool apply, out string detail)
        {
            detail = null;
            MyEntity e;
            MyCharacter c = MyEntities.TryGetEntityById((long)r.targetEntityId, out e, false) ? e as MyCharacter : null;
            if (c == null || c.Closed) { detail = "character " + (long)r.targetEntityId + " not found"; return DamageReject.NotApplied; }
            MySession session = MySession.Static;
            bool creative = session != null && session.CreativeMode;
            string reason;
            if (!CombatGate.Character(creative, MyCharacter.CharactersCanDie, c.CharacterCanDie, out reason))
            {
                if (_characterInactive != reason)
                {
                    _characterInactive = reason;
                    DeLog.Info("DE.Damage INACTIVE character: " + reason + " (records rejected CannotDie; S6.M4 shows this gate)");
                }
                detail = reason;
                return DamageReject.CannotDie;
            }
            if (a.SeType == null) { detail = "type " + r.snDamageType + " not forwarded"; return DamageReject.Type; }
            MyEntityStat health = c.StatComp != null ? c.StatComp.Health : null;
            if (health == null) { detail = "no health stat"; return DamageReject.NotApplied; }
            double before = health.Value;
            a.HpBefore = before;
            if (!apply)
            {
                a.HpAfter = before;
                return DamageReject.None;
            }
            bool ok = c.DoDamage((float)a.Se, MyStringHash.GetOrCompute(a.SeType), true, 0L);
            double after = health.Value;
            a.HpAfter = after;
            a.AppliedAmount = before - after;
            if (!ok || !(a.AppliedAmount > 0))
            {
                detail = "MyCharacter.DoDamage " + (ok ? "applied nothing" : "returned false") + " (a safe zone, admin invulnerability, or dead)";
                return DamageReject.NotApplied;
            }
            return DamageReject.None;
        }

        // ============================================================================================= de.s6 damage

 /// <summary>The next sequence number for a test record without one (above everything the window has seen).</summary>
        public static uint NextInjectSeq()
        {
            uint s = Math.Max(Dedup.HighWater, _injectSeq) + 1;
            _injectSeq = s;
            return s;
        }

        /// <summary>The generation and epoch a test record defaults to (the link's, else the window's).</summary>
        public static void Defaults(Link link, out uint generation, out uint worldEpoch)
        {
            generation = link != null && link.State == LinkState.Up ? link.LastSnGeneration : Dedup.Generation;
            worldEpoch = Phases.WorldEpoch;
        }

        /// <summary>
 /// <c>de.s6 damage</c>: one record through the same checks as C8 records, applied only when <paramref name="apply"/>
 /// (a dry run marks no id and changes nothing). The window follows the record's generation when no link is up.
 /// </summary>
        public static bool Inject(DamageRecord r, bool apply, out Dictionary<string, object> result, bool seqExplicit = true)
        {
            Injected++;
            Link link = Plugin.Instance != null ? Plugin.Instance.LinkForS1 : null;
            uint gen, epoch;
            Defaults(link, out gen, out epoch);
            // review F9: an applied test record without an explicit seq would mark hw+1 in Subnautica's live generation and
 // make its next real hit a Duplicate; refused while the link is up (dry runs and the fake host are unaffected)
            if (apply && !seqExplicit && link != null && link.State == LinkState.Up)
            {
                result = new Dictionary<string, object>(StringComparer.Ordinal) { { "outcome", "refused" }, { "detail", "link up: an applied test record needs an explicit seq (and gen); use a dry run or the fake host" } };
                return false;
            }
            if (apply && (!_genKnown || Dedup.Generation != gen))
            {
                Dedup.Reset(gen);
                _genKnown = true;
            }
            Dedup.Bypass = S6Mutants.DedupOff;
            DamageReject why;
            try { why = Process(r, gen, epoch, apply, true, Wire.Native.Win32.TickCount64(), out result); }
            catch (Exception e)
            {
                Errors++;
                result = new Dictionary<string, object>(StringComparer.Ordinal) { { "outcome", "error" }, { "detail", e.GetType().Name + ": " + e.Message } };
                return false;
            }
            return why == DamageReject.None;
        }

        // ============================================================================================= status, stats

        public static Dictionary<string, object> RejectCounts()
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 1; i < Rejected.Length; i++)
                if (Rejected[i] > 0) d[((DamageReject)i).ToString()] = Rejected[i];
            return d;
        }

        public static long RejectedTotal()
        {
            long n = 0;
            for (int i = 0; i < Rejected.Length; i++) n += Rejected[i];
            return n;
        }

        public static Dictionary<string, object> Status()
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            d["drained"] = Drained;
            d["applied"] = Applied;
            d["appliedGrid"] = AppliedGrid;
            d["appliedCharacter"] = AppliedCharacter;
            d["appliedSe"] = AppliedSeTotal;
            d["rejected"] = RejectCounts();
            d["rejectedTotal"] = RejectedTotal();
            d["rejectRowsSuppressed"] = RejectRows.Suppressed;
            d["generation"] = Dedup.Generation;
            d["highWater"] = Dedup.HighWater;
            d["admitted"] = Dedup.Admitted;
            d["duplicates"] = Dedup.Duplicates;
            d["tooOld"] = Dedup.TooOld;
            d["reordered"] = Dedup.Reordered;
            d["gaps"] = Dedup.Gaps;
            d["dedupBypass"] = Dedup.Bypass;
            d["scaleMismatch"] = ScaleMismatch;
            d["injected"] = Injected;
            d["injectedApplied"] = InjectedApplied;
            d["errors"] = Errors;
            d["blocksInactive"] = _blocksInactive;
            d["characterInactive"] = _characterInactive;
            Link link = Plugin.Instance != null ? Plugin.Instance.LinkForS1 : null;
            d["ringCorrupt"] = link != null ? link.DamageRingCorrupt : 0;
            d["gate"] = Gate();
            return d;
        }

        /// <summary>The world's damage gates as SE sees them now (de.s6 status; the launcher checks them before n1).</summary>
        public static Dictionary<string, object> Gate()
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            MySession s = MySession.Static;
            if (s == null || s.Settings == null) { d["session"] = false; return d; }
            d["session"] = true;
            d["mode"] = s.CreativeMode ? "Creative" : s.SurvivalMode ? "Survival" : "other";
            d["destructibleBlocks"] = s.Settings.DestructibleBlocks;
            bool can = MyCharacter.CharactersCanDie;
            d["charactersCanDie"] = can;
            MyCharacter c = s.LocalCharacter;
            string why;
            d["characterOk"] = c != null && CombatGate.Character(s.CreativeMode, can, c.CharacterCanDie, out why);
            d["characterReason"] = c == null ? "no local character" : (CombatGate.Character(s.CreativeMode, can, c.CharacterCanDie, out why) ? null : why);
            d["characterId"] = CharacterExport.CurrentCharacterId.ToString(CultureInfo.InvariantCulture);
            return d;
        }

        /// <summary>s6.stats once per second while something changed.</summary>
        private static void StatsRow(ulong nowMs, Link link)
        {
            if (nowMs < _nextStatsMs) return;
            _nextStatsMs = nowMs + S6Settings.StatsRowMs;
            bool hasSn = link != null && link.HasSnStatus;
            SnStatus st = hasSn ? link.LastSnStatus : default(SnStatus);
            long corrupt = link != null ? link.DamageRingCorrupt : 0;
            string key = Drained + "|" + Applied + "|" + RejectedTotal() + "|" + Dedup.Admitted + "|" + ScaleMismatch + "|" + corrupt + "|" +
                         CollisionSkin.SentCount + "|" + CollisionSkin.TooLargeCount + "|" + CollisionSkin.RebuildCount + "|" + CollisionSkin.Building + "|" +
                         st.capabilities + "|" + (st.flags & SnStatusFlags.DamageSelfTestPass) + "|" + st.fixedDeltaTime.ToString("R", CultureInfo.InvariantCulture) + "|" +
                         st.damageDropped + "|" + st.damageOverflowDepth;
            if (key == _lastStatsKey) return;
            bool first = _lastStatsKey == null;
            _lastStatsKey = key;
            if (first && Drained == 0 && CollisionSkin.SentCount == 0 && !hasSn) return;
            var sb = S6Rows.Head(Utc(), "stats");
            S5Rows.Int(sb, "drained", Drained);
            S5Rows.Int(sb, "applied", Applied);
            sb.Append(",\"rejected\":").Append(JsonText.Serialize(RejectCounts()));
            S5Rows.Int(sb, "admitted", Dedup.Admitted);
            S5Rows.Int(sb, "duplicates", Dedup.Duplicates);
            S5Rows.Int(sb, "tooOld", Dedup.TooOld);
            S5Rows.Int(sb, "reordered", Dedup.Reordered);
            S5Rows.Int(sb, "gaps", Dedup.Gaps);
            S5Rows.Int(sb, "scaleMismatch", ScaleMismatch);
            S5Rows.Int(sb, "ringCorrupt", corrupt);
            sb.Append(",\"skins\":{\"sent\":").Append(CollisionSkin.SentCount).Append(",\"bytes\":").Append(CollisionSkin.SentBytes)
              .Append(",\"tooLarge\":").Append(CollisionSkin.TooLargeCount).Append(",\"rebuilds\":").Append(CollisionSkin.RebuildCount)
              .Append(",\"building\":").Append(CollisionSkin.Building ? "true" : "false").Append('}');
            sb.Append(",\"sn\":{\"caps\":").Append(st.capabilities).Append(",\"selfTest\":").Append((st.flags & SnStatusFlags.DamageSelfTestPass) != 0 ? "true" : "false")
              .Append(",\"fixedDt\":").Append(S3Rows.G(st.fixedDeltaTime)).Append(",\"damageDropped\":").Append(st.damageDropped)
              .Append(",\"overflowDepth\":").Append(st.damageOverflowDepth).Append(",\"present\":").Append(hasSn ? "true" : "false").Append('}');
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
        }

        private static string Utc() { return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); }
    }
}
