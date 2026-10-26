using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;

namespace DeepEngineers.SN
{
    // =================================================================================================================
 // S6 "Collision B + combat", Subnautica side, Unity-free part (compiled into DeepEngineers.SN.Tests): the S6 switch
 // and seeded failures, the SkinSet store fed by MirrorState (C4 type 6), the emit rule of the TakeDamage postfix and
 // the C8 record build pass (CONTRACT §4.2, §5.4 C8), the hit-point frames (§2.7), the 256-shapes round-robin budget
 // and the physics-rate hysteresis (§2.6), the Freedom gate line (§5.4 "C7 for S6"), the self-check bookkeeping
 // (§4.2, S6.N9), the spawn arguments (de.spawn), the penetration streaks and step-time statistics of S6.B-T1, the
 // synthetic skins of the local B-T1 proxy and the s6.* row helpers. The Unity halves live next to this file
 // (S6Host, ProxySections, CharacterProxy, DamagePatches, DamageSink, S6Scene,...).
 // =================================================================================================================

 /// <summary>The S6 switch: env <c>DEEPENGINEERS_S6=on|off</c> (default on).</summary>
    internal static class S6Switches
    {
        public const string Var = "DEEPENGINEERS_S6";

        public static bool Enabled
        {
            get
            {
                string v = null;
                try { v = Environment.GetEnvironmentVariable(Var); } catch (Exception) { }
                return !IsOff(v);
            }
        }

        public static bool IsOff(string v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            v = v.Trim().ToLowerInvariant();
            return v == "off" || v == "0" || v == "false" || v == "no";
        }
    }

    /// <summary>
 /// S6 seeded failures of the Subnautica side (ROADMAP S6 "Seeded failures"; switched like every SN mutant by
 /// <c>DEEPENGINEERS_MUTANT</c> or <c>de.mutant {name, on}</c>, known to <see cref="DevMutants"/>).
 /// </summary>
    internal static class S6Mutants
    {
        /// <summary>S6.M1: proxy colliders are triggers (creatures pass through: B-T1 (1) red).</summary>
        public const string Triggers = "S6.M1";
        /// <summary>S6.M2: the MeleeAttack.CanDealDamageTo postfix is not installed (or no-ops): B-T1 (2) gets no bites.</summary>
        public const string NoCanDeal = "S6.M2";
        /// <summary>S6.M5: the G1-E3 probe colliders under a scaled Frame with the Entity rotated 30°.</summary>
        public const string ProbeUnderFrame = "S6.M5";
        /// <summary>S6.M8: the character capsule on a child of the locked Player (no contacts: B-T1e gets no character bites).</summary>
        public const string CapsuleOnPlayer = "S6.M8";
        /// <summary>S6.M9: no Physics.SyncTransforms before the plugin's own queries (the G1-E3 re-pose reads the stale pose).</summary>
        public const string NoSync = "S6.M9";
        /// <summary>S6.M10: records built inside a DamageSystem.RadiusDamage context dropped before C8 (S6.N5 red).</summary>
        public const string DropRadius = "S6.M10";
        /// <summary>S6.M11: a subgrid section's localHit left in the root grid's frame (id and flag kept: S6.N8 wrong block).</summary>
        public const string RootFrameHit = "S6.M11";
        /// <summary>S6.M12: TakeDamage patch off; a late AddComponent IOnTakeDamage forwarder instead (S6.N9 red: no Player record).</summary>
        public const string LateForwarder = "S6.M12";

        public static readonly string[] All = { Triggers, NoCanDeal, ProbeUnderFrame, CapsuleOnPlayer, NoSync, DropRadius, RootFrameHit, LateForwarder };

        public static bool IsOn(string name) { return DevMutants.IsOn(name); }

        public static string Describe()
        {
            var on = new List<string>();
            foreach (string n in All) if (DevMutants.IsOn(n)) on.Add(n);
            return on.Count == 0 ? "none" : string.Join(",", on.ToArray());
        }
    }

    // ------------------------------------------------------------------------------------------------ C4 SkinSet store

 /// <summary>One entity's applied skin (the complete SkinSet it replaced the earlier one with).</summary>
    internal sealed class SkinEntry
    {
        public ulong EntityId;
        public uint Version;
        public readonly List<SkinSectionData> Sections = new List<SkinSectionData>();
        public int Boxes;
        public int Serial;
    }

    internal enum SkinFeed { Applied = 0, Stale = 1, Bad = 2, Dropped = 3 }

    /// <summary>
 /// C4 type 6 SkinSet records as <see cref="MirrorState.Feed"/> receives them (CONTRACT §5.4 "C4 type 6 SkinSet
 /// (S6)"): decoded and checked whole by Wire <see cref="SkinCodec"/> (a bad message is dropped whole and counted
 /// <c>skinBad</c>); a version ≤ the applied one is ignored (<c>skinStale</c>); a section of kind 2 is ignored and counted;
 /// EntityRemove and every MirrorState.Clear (ClearAll, new SE generation, new world epoch) drop the skins, so the
 /// version restarts at 1. The S6 host listens through <see cref="Changed"/>, <see cref="Removed"/> and
 /// <see cref="Cleared"/>. Main thread only.
 /// </summary>
    internal sealed unsafe class SkinStore
    {
        public readonly Dictionary<ulong, SkinEntry> Skins = new Dictionary<ulong, SkinEntry>();
        public long Received, Applied, Stale, Bad, KindIgnored, EpochDropped, RemovedCount, ClearedCount;
        public string LastError;
        private int _serial;

        public Action<SkinEntry> Changed;
        public Action<ulong> Removed;
        public Action Cleared;

        /// <summary>Feeds one C4 type 6 payload (<paramref name="bytes"/> without the 8-byte message header).</summary>
        public SkinFeed Feed(byte* p, int bytes, HashSet<ulong> droppedGrids, out ulong entityId)
        {
            Received++;
            entityId = bytes >= 8 ? *(ulong*)p : 0UL;
            if (droppedGrids != null && entityId != 0 && droppedGrids.Contains(entityId)) { EpochDropped++; return SkinFeed.Dropped; }
            var sections = new List<SkinSectionData>();
            SkinSetHeader h;
            string error;
            bool ok;
            int ignored = 0;
            try { ok = SkinCodec.TryDecode(p, bytes, out h, sections, out ignored, out error); }
            catch (Exception e) { ok = false; h = default(SkinSetHeader); error = e.GetType().Name + ": " + e.Message; }
            KindIgnored += ignored;
            if (!ok)
            {
                Bad++;
                LastError = "SkinSet " + DeLog.Hex(entityId) + " of " + bytes + " B: " + error;
                return SkinFeed.Bad;
            }
            return Apply(h.entityId, h.skinVersion, sections, out entityId);
        }

        /// <summary>Applies an already decoded skin (also used by tests and the local synthetic proxy).</summary>
        public SkinFeed Apply(ulong id, uint version, List<SkinSectionData> sections, out ulong entityId)
        {
            entityId = id;
            SkinEntry e;
            if (Skins.TryGetValue(id, out e) && version <= e.Version) { Stale++; return SkinFeed.Stale; }
            if (e == null) { e = new SkinEntry { EntityId = id }; Skins[id] = e; }
            e.Version = version;
            e.Sections.Clear();
            e.Boxes = 0;
            foreach (SkinSectionData s in sections)
            {
                if (s.Kind != SkinSectionKind.Grid) { KindIgnored++; continue; }
                e.Sections.Add(s);
                e.Boxes += s.Boxes.Count;
            }
            e.Serial = ++_serial;
            Applied++;
            Action<SkinEntry> cb = Changed;
            if (cb != null) cb(e);
            return SkinFeed.Applied;
        }

        public void Remove(ulong id)
        {
            if (!Skins.Remove(id)) return;
            RemovedCount++;
            Action<ulong> cb = Removed;
            if (cb != null) cb(id);
        }

        public void Clear()
        {
            bool any = Skins.Count > 0;
            Skins.Clear();
            ClearedCount++;
            Action cb = Cleared;
            if (any && cb != null) cb();
        }
    }

    // ------------------------------------------------------------------------------------------------ emit rule and records

 /// <summary>Why a TakeDamage call on one of our targets did not become a C8 record (CONTRACT §4.2 "Emit rule").</summary>
    internal enum S6Skip
    {
        None = 0, Gate = 1, ZeroAmount = 2, TypeNotForwarded = 3, Refused = 4, PlayerNotAtCharacter = 5, NoCharacter = 6, RadiusMutant = 7,
        PlayerShadowed = 8,   // review F10: a blast/shock on the ghost Player while the character section is active (the section's record counts)
    }

    /// <summary>Which hit-point source the postfix used (CONTRACT §2.7 "The hit point w").</summary>
    internal enum HitVia { Mouth = 0, Shocker = 1, Radius = 2, Dealer = 3, Position = 4, Root = 5, Player = 6 }

    internal static class S6Emit
    {
        public static string SkipName(S6Skip s)
        {
            switch (s)
            {
                case S6Skip.None: return "none";
                case S6Skip.Gate: return "gate";
                case S6Skip.ZeroAmount: return "zeroAmount";
                case S6Skip.TypeNotForwarded: return "typeNotForwarded";
                case S6Skip.Refused: return "refused";
                case S6Skip.PlayerNotAtCharacter: return "playerNotAtCharacter";
                case S6Skip.NoCharacter: return "noCharacter";
                case S6Skip.RadiusMutant: return "radiusDropped";
                default: return "skip" + ((int)s).ToString(CultureInfo.InvariantCulture);
            }
        }

        public static string ViaName(HitVia v)
        {
            switch (v)
            {
                case HitVia.Mouth: return "mouth";
                case HitVia.Shocker: return "shocker";
                case HitVia.Radius: return "radius";
                case HitVia.Dealer: return "dealer";
                case HitVia.Position: return "position";
                case HitVia.Root: return "root";
                default: return "player";
            }
        }

        /// <summary>Subnautica DamageType values that are forwarded (Normal 0, Explosive 7, Electrical 8; enum VERIFIED DamageType.cs).</summary>
        public static bool Forwarded(int snDamageType)
        {
            return snDamageType == CombatConventions.SnDamageForward.Normal || snDamageType == CombatConventions.SnDamageForward.Explosive ||
                   snDamageType == CombatConventions.SnDamageForward.Electrical;
        }

        /// <summary>
 /// The emit rule after the target was recognised: the game's gate (prefix), a finite positive amount, a forwarded
 /// type, the Freedom gate, and for the Player the ghost rule (character present, not seated, the Player placed at it
 /// this frame). Capture mode (the self-check) skips the Freedom and ghost rules: it checks the patch path itself.
 /// </summary>
        public static S6Skip Check(bool gamePassed, float amount, int type, bool freedomPass, bool isPlayer, bool playerAtCharacter, bool capture)
        {
            if (!gamePassed) return S6Skip.Gate;
            if (float.IsNaN(amount) || float.IsInfinity(amount) || !(amount > 0f)) return S6Skip.ZeroAmount;
            if (!Forwarded(type)) return S6Skip.TypeNotForwarded;
            if (capture) return S6Skip.None;
            if (!freedomPass) return S6Skip.Refused;
            if (isPlayer && !playerAtCharacter) return S6Skip.PlayerNotAtCharacter;
            return S6Skip.None;
        }

        /// <summary>C8 flags for a hit source (layout-pinned <see cref="DamageFlags"/> plus S6 <c>GhostPlayer</c>).</summary>
        public static ushort Flags(HitVia via, bool subgrid, bool ghostPlayer)
        {
            int f = 0;
            switch (via)
            {
                case HitVia.Mouth: f |= DamageFlags.MouthContact; break;
                case HitVia.Radius: f |= DamageFlags.Radius | DamageFlags.FallbackHitPoint; break;
                case HitVia.Shocker:
                case HitVia.Dealer:
                case HitVia.Position:
                case HitVia.Root: f |= DamageFlags.FallbackHitPoint; break;
                case HitVia.Player: f |= DamageFlags.FallbackHitPoint; break;
            }
            if (subgrid) f |= DamageFlags.FrameSubgrid;
            if (ghostPlayer) f |= CombatConventions.C8FlagsExt.GhostPlayer;
            return (ushort)f;
        }

        /// <summary>The factor Subnautica expects (seScaleExpected): 25 for a large-grid section, 1 for small, 1 for the character.</summary>
        public static float ScaleExpected(uint targetKind, uint targetClass)
        {
            if (targetKind == DamageTargetKind.Character) return (float)CombatConventions.DamageScale.KChar;
            return targetClass == GridClass.LargeGrid ? (float)CombatConventions.DamageScale.KLarge : (float)CombatConventions.DamageScale.KSmall;
        }
    }

    /// <summary>Everything one C8 record carries besides the producer's counters (CONTRACT §5.4 "C8 field meanings").</summary>
    internal struct HitInput
    {
        public float Amount;
        public int Type;
        public ushort Flags;
        public uint TargetKind, SectionId, TargetClass;
        public ulong TargetEntityId, RootEntityId, SnapshotTick;
        public Vec3 LocalHit, WorldHit, DealerPoint;
        public bool HasDealerPoint;
        public int DealerTechType;
        public uint DealerInstanceId;
        public double SnTimeSec;
    }

    /// <summary>
 /// The C8 record build pass: dedupSeq 1, 2, 3… per Subnautica generation (+1 per record built; 0 never sent), the
 /// writer's generation, unit SN_HP, the expected factor, the hello's world epoch. Captured self-check records do not
 /// take a sequence number (they never reach C8, so SE sees no gap).
 /// </summary>
    internal sealed class DamageRecordBuilder
    {
        private uint _seq;
        public uint Generation { get; private set; }
        public uint LastSeq { get { return _seq; } }

        public DamageRecordBuilder(uint generation) { Generation = generation; }

        /// <summary>A new Subnautica generation restarts the numbering (never happens within a process; kept for tests).</summary>
        public void Reset(uint generation) { Generation = generation; _seq = 0; }

        /// <summary>S11 hook: the next dedupSeq for an S11 record built outside <see cref="Build"/> (DamageSink.EmitPrepared).</summary>
        public uint NextSeq() { return ++_seq; }

        public DamageRecord Build(ref HitInput h, uint worldEpoch, bool capture)
        {
            var r = default(DamageRecord);
            r.dedupSeq = capture ? 0u : ++_seq;
            r.snGeneration = Generation;
            r.unit = DamageUnit.SnHp;
            r.amount = h.Amount;
            r.seScaleExpected = S6Emit.ScaleExpected(h.TargetKind, h.TargetClass);
            r.snDamageType = (ushort)h.Type;
            r.flags = h.Flags;
            r.targetKind = h.TargetKind;
            r.sectionId = h.TargetKind == DamageTargetKind.Character ? 0u : h.SectionId;
            r.targetEntityId = h.TargetEntityId;
            r.rootEntityId = h.RootEntityId;
            r.snapshotTick = h.SnapshotTick;
            r.localHit = new F32x3((float)h.LocalHit.X, (float)h.LocalHit.Y, (float)h.LocalHit.Z);
            r.snWorldHit = new F32x3((float)h.WorldHit.X, (float)h.WorldHit.Y, (float)h.WorldHit.Z);
            Vec3 d = h.HasDealerPoint ? h.WorldHit - h.DealerPoint : Vec3.Zero;
            double len = d.Length;
            Vec3 u = len > 1e-6 ? d / len : Vec3.Zero;
            r.attackDirSn = new F32x3((float)u.X, (float)u.Y, (float)u.Z);
            r.dealerTechType = h.DealerTechType;
            r.dealerInstanceId = h.DealerInstanceId;
            r.worldEpoch = worldEpoch;
            r.snTimeSec = h.SnTimeSec;
            return r;
        }
    }

    /// <summary>
 /// The local hit point (CONTRACT §2.7): <c>ToLocal</c> with the (o, q, s) that placed the section at the last physics
 /// step, then <c>InvertRigid(rel)</c> for a subgrid section; the character with its own (charO, charQ, charS).
 /// S6.M11 leaves a subgrid hit in the root grid's frame (the record keeps the subgrid's id and flag).
 /// </summary>
    internal static class HitFrames
    {
        public static Vec3 GridLocal(Vec3 o, Quat q, double s, SeMatrix rel, bool subgrid, Vec3 w, bool rootFrameMutant)
        {
            if (subgrid && rootFrameMutant) return WireMath.ToLocal(o, q, s, w);
            return CollisionSkinMath.HitLocal(o, q, s, rel, subgrid, w);
        }

        public static Vec3 CharacterLocal(Vec3 o, Quat q, double s, Vec3 w) { return WireMath.ToLocal(o, q, s, w); }
    }

    // ------------------------------------------------------------------------------------------------ physics-step budget

 /// <summary>
 /// The shape budget of CONTRACT §2.6 as a work queue: at most <see cref="CombatConventions.Limits.MaxShapesPerStep"/>
 /// collider creations or refreshes per physics step, granted by Wire <see cref="ShapeBudget.Plan"/> (round robin from a
 /// cursor; a 1,000-box skin takes 4 steps). A job is (item, total shapes); a job enqueued again restarts in place (a
 /// newer pose or skin supersedes the pass); finished jobs leave the queue.
 /// </summary>
    internal sealed class ShapeQueue<T> where T : class
    {
        private sealed class Job { public T Item; public int Total, Done; }
        private readonly List<Job> _jobs = new List<Job>();
        private readonly List<Job> _done = new List<Job>(); // reused each step: no allocation in the physics step (integrator S6)
        private int _cursor;
        private int[] _pending = new int[16], _grant = new int[16];
        public long ShapesTotal, Steps, Completed;

        public int Pending { get { return _jobs.Count; } }

        private int IndexOf(T item)
        {
            for (int i = 0; i < _jobs.Count; i++) if (ReferenceEquals(_jobs[i].Item, item)) return i;
            return -1;
        }

        public bool Contains(T item) { return IndexOf(item) >= 0; }

        public void Enqueue(T item, int total)
        {
            int i = IndexOf(item);
            if (i >= 0) { _jobs[i].Total = total; _jobs[i].Done = 0; return; }
            _jobs.Add(new Job { Item = item, Total = total });
        }

        public void Cancel(T item)
        {
            int i = IndexOf(item);
            if (i < 0) return;
            RemoveAt(i);
        }

        private void RemoveAt(int i)
        {
            _jobs.RemoveAt(i);
            if (i < _cursor) _cursor--;
            if (_cursor >= _jobs.Count) _cursor = 0;
        }

        public void Clear() { _jobs.Clear(); _cursor = 0; }

        /// <summary>
 /// One physics step: <paramref name="apply"/>(item, first, count) for this step's grants (at most
 /// <paramref name="budget"/> shapes in all); a job that finishes calls <paramref name="completed"/> and leaves.
 /// Returns the shapes handled.
 /// </summary>
        public int Step(int budget, Action<T, int, int> apply, Action<T> completed)
        {
            int n = _jobs.Count;
            Steps++;
            if (n == 0) return 0;
            if (_pending.Length < n) { _pending = new int[n * 2]; _grant = new int[n * 2]; }
            for (int i = 0; i < n; i++) _pending[i] = Math.Max(0, _jobs[i].Total - _jobs[i].Done);
            _cursor = ShapeBudget.Plan(_pending, n, _cursor, budget, _grant);
            int used = 0;
            _done.Clear();
            for (int i = 0; i < n; i++)
            {
                Job j = _jobs[i];
                int g = _grant[i];
                if (g > 0)
                {
                    apply(j.Item, j.Done, g);
                    j.Done += g;
                    used += g;
                }
                if (j.Done >= j.Total) _done.Add(j);
            }
            for (int k = 0; k < _done.Count; k++)
            {
                Job j = _done[k];
                int i = _jobs.IndexOf(j);
                if (i >= 0) RemoveAt(i);
                Completed++;
                if (completed != null) completed(j.Item);
            }
            _done.Clear();
            ShapesTotal += used;
            return used;
        }
    }

    /// <summary>
 /// The physics-rate decision of CONTRACT §2.6: register the proxy stepper with
 /// <c>RegisterHighFixedTimestepBehavior</c> as soon as a moving proxy is within 200 m of the camera, deregister after
 /// 2 s with none. +1 register, −1 deregister, 0 nothing.
 /// </summary>
    internal sealed class PhysicsRateRule
    {
        public const double HoldSeconds = 2.0;
        public const float NearRadiusM = 200f;
        public bool Registered { get; private set; }
        private double _lastAnySec = double.NegativeInfinity;

        public int Update(bool anyMovingNear, double nowSec)
        {
            if (anyMovingNear) _lastAnySec = nowSec;
            if (anyMovingNear && !Registered) { Registered = true; return +1; }
            if (!anyMovingNear && Registered && nowSec - _lastAnySec >= HoldSeconds) { Registered = false; return -1; }
            return 0;
        }

        /// <summary>World unload, teardown, OnDestroy: always deregister.</summary>
        public bool ForceOff()
        {
            bool was = Registered;
            Registered = false;
            _lastAnySec = double.NegativeInfinity;
            return was;
        }
    }

    /// <summary>
 /// A proxy section "moves" when its target pose changed by more than 1 mm or 0.05° (CONTRACT §2.6), compared with the
 /// anchor pose of its last move. It counts as moving for <see cref="WindowSeconds"/>
 /// after that change (one C4 cadence gap); the physics rate then holds its 2 s (<see cref="PhysicsRateRule.HoldSeconds"/>)
 /// — S6-1: with 2 s here the two windows stacked and the rate fell back 4 s after a hull stopped (S6.N7).
 /// </summary>
    internal static class MotionRule
    {
        public const double MoveM = 0.001, MoveDeg = 0.05, WindowSeconds = 0.25;

        public static double AngleDeg(Quat a, Quat b)
        {
            double d = Math.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W);
            if (d > 1) d = 1;
            return 2.0 * Math.Acos(d) * 180.0 / Math.PI;
        }

        public static bool Moved(Vec3 p0, Quat q0, Vec3 p1, Quat q1)
        {
            return Vec3.Distance(p0, p1) > MoveM || AngleDeg(q0, q1) > MoveDeg;
        }

        /// <summary>Teleport instead of sweep: more than 2 m or 30° between the body and its target (CONTRACT §2.6).</summary>
        public static bool Teleport(Vec3 body, Quat bodyQ, Vec3 target, Quat targetQ)
        {
            return Vec3.Distance(body, target) > 2.0 || AngleDeg(bodyQ, targetQ) > 30.0;
        }
    }

    // ------------------------------------------------------------------------------------------------ Freedom gate

 /// <summary>The Freedom gate (CONTRACT §5.4 "C7 for S6"; S6.N2): mode Freedom (NoSurvival, 2) and not invisible.</summary>
    internal static class FreedomRules
    {
        public static bool Evaluate(int mode, int cheats, bool invisible, out string line)
        {
            HostModeCheck c = HostSaveRules.CheckLoaded(mode, cheats, invisible);
            line = "DE.S6 freedom mode=" + GameModeOptionBits.Name(mode) + " invisible=" + (invisible ? "true" : "false") +
                   (cheats != 0 ? " cheats=0x" + cheats.ToString("x", CultureInfo.InvariantCulture) : "") +
                   (c.Ok ? " PASS" : " REFUSED (" + c.Reason + (c.Reason == "mode" ? ": Freedom required" : ": NoAggression, creatures never attack") + ")");
            return c.Ok;
        }
    }

    // ------------------------------------------------------------------------------------------------ self-check

 /// <summary>
 /// S6.N9 bookkeeping (CONTRACT §4.2 "Start-up self-check"): pass = exactly one captured record for the test section
 /// (kind 1) and exactly one for the Player (kind 2), each amount 1 and type Normal.
 /// </summary>
    internal static class SelfCheckBook
    {
        public static bool Judge(IList<DamageRecord> captured, out int sectionRecords, out int playerRecords, out string detail)
        {
            sectionRecords = 0;
            playerRecords = 0;
            var bad = new List<string>();
            foreach (DamageRecord r in captured)
            {
                if (r.targetKind == DamageTargetKind.GridSection) sectionRecords++;
                else if (r.targetKind == DamageTargetKind.Character) playerRecords++;
                else bad.Add("kind " + r.targetKind);
                if (r.amount != 1f) bad.Add("amount " + r.amount.ToString("R", CultureInfo.InvariantCulture));
                if (r.snDamageType != CombatConventions.SnDamageForward.Normal) bad.Add("type " + r.snDamageType);
                if (r.unit != DamageUnit.SnHp) bad.Add("unit " + r.unit);
                if (r.targetKind == DamageTargetKind.Character && (r.flags & CombatConventions.C8FlagsExt.GhostPlayer) == 0) bad.Add("player record without GhostPlayer");
            }
            bool pass = sectionRecords == 1 && playerRecords == 1 && bad.Count == 0;
            detail = "section=" + sectionRecords + " player=" + playerRecords + (bad.Count > 0 ? " bad=" + string.Join(";", bad.ToArray()) : "") +
                     (pass ? "" : sectionRecords == 0 && playerRecords == 0 ? " (no record: TakeDamage patch missing or not reached)" :
                                  playerRecords == 0 ? " (no Player record)" : sectionRecords == 0 ? " (no section record)" : "");
            return pass;
        }
    }

    // ------------------------------------------------------------------------------------------------ spawn

 /// <summary>de.spawn arguments: <c>{techType, count 1..20, dist 1..100}</c> or argv [techType, n, dist].</summary>
    internal static class SpawnArgs
    {
        public const int MaxCount = 20, MaxDist = 100;

        /// <summary>Parses and validates; the TechType name is matched ignoring case against <paramref name="names"/> (the enum's names).</summary>
        public static bool TryParse(Dictionary<string, object> a, IList<string> names, out string techType, out int count, out int dist, out string error)
        {
            techType = null; count = 1; dist = 12; error = null;
            string t = S3Args.Str(a, "techType", null) ?? S3Args.Str(a, "type", null);
            if (string.IsNullOrEmpty(t)) { error = "techType required"; return false; }
            string canonical = null;
            if (names != null)
                foreach (string n in names)
                    if (string.Equals(n, t.Trim(), StringComparison.OrdinalIgnoreCase)) { canonical = n; break; }
            if (canonical == null) { error = "unknown TechType '" + t + "'"; return false; }
            double c = S3Args.Num(a, "count", S3Args.Num(a, "n", 1));
            double d = S3Args.Num(a, "dist", 12);
            if (!(c >= 1 && c <= MaxCount) || c != Math.Floor(c)) { error = "count must be 1.." + MaxCount; return false; }
            if (!(d >= 1 && d <= MaxDist)) { error = "dist must be 1.." + MaxDist; return false; }
            techType = canonical;
            count = (int)c;
            dist = (int)Math.Round(d);
            return true;
        }

        /// <summary>argv [techType, n, dist] (the SE chat relay) into a dictionary.</summary>
        public static Dictionary<string, object> FromArgv(List<object> argv)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            if (argv == null) return d;
            int pos = 0;
            foreach (object o in argv)
            {
                string s = o as string;
                if (s != null && s.IndexOf('=') > 0)
                {
                    int eq = s.IndexOf('=');
                    double v;
                    string k = s.Substring(0, eq), val = s.Substring(eq + 1);
                    if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) d[k] = v; else d[k] = val;
                    continue;
                }
                if (pos == 0) d["techType"] = s ?? Convert.ToString(o, CultureInfo.InvariantCulture);
                else if (pos == 1) d["count"] = o;
                else if (pos == 2) d["dist"] = o;
                pos++;
            }
            return d;
        }

        /// <summary>The console line (VERIFIED SpawnConsoleCommand: "spawn &lt;TechType&gt; [n] [maxDist]"; dist as an integer: the game parses it with the current culture).</summary>
        public static string Command(string techType, int count, int dist)
        {
            return "spawn " + techType + " " + count.ToString(CultureInfo.InvariantCulture) + " " + dist.ToString(CultureInfo.InvariantCulture);
        }
    }

    // ------------------------------------------------------------------------------------------------ B-T1 statistics

 /// <summary>
 /// S6.B-T1 (1): per (creature collider, section) pair, the run of consecutive physics steps with a penetration
 /// deeper than 0.1 m. Fail = a run longer than 3 steps.
 /// </summary>
    internal sealed class PenetrationStreaks
    {
        public const double DeepM = 0.1, RowM = 0.05;
        public const int MaxStreak = 3;
        private readonly Dictionary<long, int> _streak = new Dictionary<long, int>();
        private readonly HashSet<long> _seenThisStep = new HashSet<long>();
        public long Samples, Rows, Deep;
        public double MaxDepthM, WindowMaxDepthM;
        public int MaxStreakSeen, WindowMaxStreak, WindowPairs;
        public long Violations;

        public static long Key(int colliderId, uint sectionKey) { return ((long)colliderId << 32) ^ sectionKey; }

        public void BeginStep() { _seenThisStep.Clear(); }

        /// <summary>One pair's depth this step; returns the pair's current streak.</summary>
        public int Note(long key, double depth)
        {
            Samples++;
            _seenThisStep.Add(key);
            if (depth > MaxDepthM) MaxDepthM = depth;
            if (depth > WindowMaxDepthM) WindowMaxDepthM = depth;
            int s;
            _streak.TryGetValue(key, out s);
            if (depth > DeepM)
            {
                s++;
                Deep++;
                if (s == MaxStreak + 1) Violations++;
            }
            else s = 0;
            _streak[key] = s;
            if (s > MaxStreakSeen) MaxStreakSeen = s;
            if (s > WindowMaxStreak) WindowMaxStreak = s;
            return s;
        }

        /// <summary>Pairs not seen this step (no overlap) end their run.</summary>
        public void EndStep()
        {
            WindowPairs = Math.Max(WindowPairs, _seenThisStep.Count);
            if (_streak.Count == 0) return;
            var drop = new List<long>();
            foreach (KeyValuePair<long, int> kv in _streak) if (!_seenThisStep.Contains(kv.Key)) drop.Add(kv.Key);
            foreach (long k in drop) _streak.Remove(k);
        }

        public void TakeWindow(out int pairs, out double maxDepth, out int maxStreak)
        {
            pairs = WindowPairs; maxDepth = WindowMaxDepthM; maxStreak = WindowMaxStreak;
            WindowPairs = 0; WindowMaxDepthM = 0; WindowMaxStreak = 0;
        }

        public bool Pass { get { return Violations == 0; } }

        public void Reset()
        {
            _streak.Clear(); _seenThisStep.Clear();
            Samples = Rows = Deep = Violations = 0;
            MaxDepthM = WindowMaxDepthM = 0;
            MaxStreakSeen = WindowMaxStreak = WindowPairs = 0;
        }
    }

    /// <summary>A window of millisecond samples with nearest-rank percentiles (s6.perf).</summary>
    internal sealed class MsWindow
    {
        private readonly double[] _v;
        private readonly double[] _scratch;
        private int _n;

        public MsWindow(int capacity = 512) { _v = new double[capacity]; _scratch = new double[capacity]; }

        public void Add(double ms) { if (_n < _v.Length) _v[_n++] = ms; }
        public int Count { get { return _n; } }

        public static int Rank(double p, int n) { int r = (int)Math.Ceiling(p * n) - 1; return r < 0 ? 0 : r >= n ? n - 1 : r; }

        public void Take(out double p50, out double p95, out double max, out int n)
        {
            n = _n;
            p50 = p95 = max = double.NaN;
            if (_n > 0)
            {
                Array.Copy(_v, _scratch, _n);
                Array.Sort(_scratch, 0, _n);
                p50 = _scratch[Rank(0.50, _n)];
                p95 = _scratch[Rank(0.95, _n)];
                max = _scratch[_n - 1];
            }
            _n = 0;
        }
    }

    // ------------------------------------------------------------------------------------------------ synthetic skins

 /// <summary>
 /// The local synthetic proxies of de.s6 bt1 {proxy: "local"} (when no stand-in producer sends one): a hull of
 /// <c>sections</c> × 10 m along SE-local x, 6 m high and 6 m wide, each section 5 × 5 × <c>nz</c> boxes
 /// (bt1: 2 sections × 25 = 50 boxes, 20 × 6 × 6 m; perf: 10 sections × 100 = 1,000 boxes, 100 × 6 × 6 m). Boxes are
 /// grid-aligned, source Synthetic; section ids from the lattice formula (ix = section index − sections/2).
 /// </summary>
    internal static class SyntheticSkins
    {
        public const double SectionLengthM = 10.0, HeightM = 6.0, WidthM = 6.0;

        public static List<SkinSectionData> Hull(int sections, int nz)
        {
            if (sections < 1 || sections > 64) throw new ArgumentOutOfRangeException("sections");
            if (nz < 1 || nz > 16) throw new ArgumentOutOfRangeException("nz");
            var list = new List<SkinSectionData>();
            double total = sections * SectionLengthM;
            const int nx = 5, ny = 5;
            double dx = SectionLengthM / nx, dy = HeightM / ny, dz = WidthM / nz;
            for (int i = 0; i < sections; i++)
            {
                double x0 = -total / 2 + i * SectionLengthM;
                var s = new SkinSectionData
                {
                    SectionId = CollisionSkinMath.SectionId(i - sections / 2, 0, 0),
                    Centre = new Vec3(x0 + SectionLengthM / 2, 0, 0),
                    Kind = SkinSectionKind.Grid,
                };
                for (int a = 0; a < nx; a++)
                    for (int b = 0; b < ny; b++)
                        for (int c = 0; c < nz; c++)
                        {
                            s.Boxes.Add(new SkinBoxData
                            {
                                Centre = new Vec3(x0 + (a + 0.5) * dx, -HeightM / 2 + (b + 0.5) * dy, -WidthM / 2 + (c + 0.5) * dz),
                                Half = new Vec3(dx / 2, dy / 2, dz / 2),
                                E0 = Vec3.UnitX, E1 = Vec3.UnitY, E2 = Vec3.UnitZ,
                                Flags = CombatConventions.SkinBoxSource.Synthetic << (int)CombatConventions.SkinBoxFlags.SourceShift,
                            });
                        }
                s.ContentHash = CollisionSkinMath.SectionContentHash(s);
                list.Add(s);
            }
            return list;
        }

        /// <summary>S6.B-T1's proxy: 20 × 6 × 6 m, 50 boxes, 2 sections.</summary>
        public static List<SkinSectionData> Bt1() { return Hull(2, 1); }

        /// <summary>B-T1 (3)'s perf proxy: <paramref name="boxes"/> rounded to whole 100-box sections (1,000 → 10 sections).</summary>
        public static List<SkinSectionData> Perf(int boxes)
        {
            int sections = Math.Max(1, Math.Min(64, (boxes + 99) / 100));
            return Hull(sections, 4);
        }

        public static int CountBoxes(IList<SkinSectionData> s) { int n = 0; foreach (SkinSectionData x in s) n += x.Boxes.Count; return n; }
    }

    /// <summary>The local proxy's loop (S6.B-T1: 5 m/s on a circle): SN-world pose at time t; the hull's long axis (SE-local x) along the path.</summary>
    internal static class LocalLoop
    {
        public static void Pose(Vec3 centre, double radius, double speed, double t, out Vec3 o, out Quat q)
        {
            double a = radius > 0 ? speed * t / radius : 0;
            o = new Vec3(centre.X + radius * Math.Cos(a), centre.Y, centre.Z + radius * Math.Sin(a));
            var tangent = new Vec3(-Math.Sin(a), 0, Math.Cos(a));
            // LookRotation(f, up) maps local +X to Cross(up, f): f = Cross(tangent, up) puts local +X on the tangent
            q = Quat.LookRotation(Vec3.Cross(tangent, Vec3.UnitY), Vec3.UnitY);
        }
    }

    // ------------------------------------------------------------------------------------------------ rows

 /// <summary>s6.* JSONL rows (one object per line, the S5 envelope: t, tickMs, frame, level, event, ev, data).</summary>
    internal static class S6Rows
    {
        public static JsonOut Begin(string ev) { return S5Rows.Begin(ev); }
        public static void End(JsonOut j) { S5Rows.End(j); }
        public static string Hex(ulong v) { return S5Rows.Hex(v); }
        /// <summary>A section id as "0x" + 8 hex digits (integrator S6: the analyzer reads strings as decimal unless 0x-prefixed;
 /// a bare "20080200" would read as decimal).</summary>
        public static string Sec(uint id) { return "0x" + id.ToString("x8", CultureInfo.InvariantCulture); }
    }
}
