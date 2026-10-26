using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S7 statics into SE (CONTRACT §2.3 staticAxes, §5.4 "S7 conventions": "C12 static primitives (S7; S7Statics)",
 // the C12 message layout, `format statics.axes`, `statics.capsule`, `statics.host`). Pure and shared by both plugins:
 // * the C12 codec (BatchStatics with the S7 version 1 in the header pad 0x06, BatchClear) and its checks: a
 // malformed message (counts, version, kind, NaN, sizes) is dropped whole and counted;
 // * the Subnautica-side rules: the Unity collider sizes (box half extents × |lossyScale|, the capsule as (radius,
 // half segment, axis), the sphere radius × max scale), the shear test (SkewApprox), "entirely above the sea",
 // the class-id hash (FNV-1a 32), the 1 cm dedup key, the grow-only batch set with its epochs, and the filter by
 // entity type with the census allow-list (never "static or kinematic": S7.M7);
 // * the SE-side geometry: box → centre F⁻¹(c), axes staticAxes(q), half extents |S⁻¹·(q·e_i)|·h_i
 // (WireMath.StaticToSe); capsule end points F⁻¹(c ∓ a·h) exactly, radius r/s(c); sphere r/s(c); the host name,
 // the host split (MaxPerHost), the create/close hysteresis (300/350 m), the census file name, the batch lattice.
 // No Unity or VRage types. Python twin: tools/worldmap/s7_ref.py (vectors tests/s7/vectors/s7_statics.tsv, read by
 // S7StaticsVectorTests).
 // =================================================================================================================

 /// <summary>Outcome of a C12 decode or of one primitive's check.</summary>
    public enum S7StaticsStatus : byte
    {
        Ok = 0,
        /// <summary>Fewer bytes than a header.</summary>
        TooShort = 1,
        /// <summary><c>payloadBytes</c> ≠ 16 + 48·count.</summary>
        BadCount = 2,
        /// <summary>count &gt; <c>StaticsLimits.MaxPerBatch</c>.</summary>
        TooMany = 3,
        /// <summary>Header pad 0x06 ≠ <c>C12Ext.Version</c>.</summary>
        BadVersion = 4,
        /// <summary>A primitive kind outside Box … MeshObb.</summary>
        BadKind = 5,
        /// <summary>A NaN or infinity in a centre, rotation or size.</summary>
        NonFinite = 6,
        /// <summary>A size ≤ 0 or larger than <see cref="S7StaticsMath.MaxHalfExtentM"/>.</summary>
        BadSize = 7,
        /// <summary>A capsule axis that is not 0, 1 or 2.</summary>
        BadAxis = 8,
        /// <summary>A rotation whose length is not 1 within 1e-3.</summary>
        BadRotation = 9,
        /// <summary>The destination array is too small for the message's count.</summary>
        BufferTooSmall = 10,
    }

    /// <summary>
 /// C12 codec (CONTRACT §5.4 "C12 messages"): type 1 <c>BatchStatics</c> = 16 B header {i16 bx, by, bz; u16 version
 /// (S7: 1, the layout's pad 0x06); u32 epoch; u32 count} + count × 48 B <see cref="StaticPrimitive"/>; type 2
 /// <c>BatchClear</c> (16 B). Little-endian, the layout structs written as is.
 /// </summary>
    public static unsafe class S7StaticsCodec
    {
        public const int HeaderBytes = 16, PrimitiveBytes = 48, ClearBytes = 16;
        /// <summary>The header pad that carries the S7 version (<c>field statics version 0x6 2 u16</c>).</summary>
        public const int VersionOffset = 0x06;

        /// <summary>16 + 48·count.</summary>
        public static int PayloadBytes(int count) { return HeaderBytes + PrimitiveBytes * count; }

        /// <summary>The largest valid payload (<c>MaxPerBatch</c> primitives: 196,624 B, below half of the 4 MiB ring).</summary>
        public static int MaxPayloadBytes { get { return PayloadBytes(S7Conventions.StaticsLimits.MaxPerBatch); } }

        /// <summary>Writes one BatchStatics payload into <paramref name="dst"/>; returns its length.</summary>
        public static int Encode(int bx, int by, int bz, uint epoch, StaticPrimitive[] prims, int count, byte[] dst)
        {
            if (dst == null) throw new ArgumentNullException("dst");
            fixed (byte* p = dst) return Encode(bx, by, bz, epoch, prims, 0, count, p, dst.Length, 0);
        }

        /// <summary>
 /// Writes one BatchStatics payload of <paramref name="count"/> primitives starting at <paramref name="start"/>;
 /// <paramref name="orFlags"/> is OR-ed into every primitive's <c>flags</c> (the census copy sets <c>Census</c>).
 /// </summary>
        public static int Encode(int bx, int by, int bz, uint epoch, StaticPrimitive[] prims, int start, int count, byte* dst, int capacity, byte orFlags)
        {
            if (count < 0 || count > S7Conventions.StaticsLimits.MaxPerBatch) throw new ArgumentOutOfRangeException("count", "0.." + S7Conventions.StaticsLimits.MaxPerBatch);
            if (count > 0 && (prims == null || start < 0 || start + count > prims.Length)) throw new ArgumentOutOfRangeException("prims");
            CheckBatchIndex(bx, "bx"); CheckBatchIndex(by, "by"); CheckBatchIndex(bz, "bz");
            int n = PayloadBytes(count);
            if (dst == null || capacity < n) throw new ArgumentException("destination smaller than " + n + " bytes");
            var h = new BatchStaticsHeader { bx = (short)bx, by = (short)by, bz = (short)bz, epoch = epoch, count = (uint)count };
            *(BatchStaticsHeader*)dst = h;
            *(ushort*)(dst + VersionOffset) = (ushort)S7Conventions.C12Ext.Version;
            for (int i = 0; i < count; i++)
            {
                StaticPrimitive sp = prims[start + i];
                sp.flags |= orFlags;
                *(StaticPrimitive*)(dst + HeaderBytes + i * PrimitiveBytes) = sp;
            }
            return n;
        }

        /// <summary>
 /// Checks and copies one BatchStatics payload. Nothing is partially accepted: any bad primitive makes the whole
 /// message malformed (CONTRACT: "a malformed message (counts, version, NaN) is dropped whole and counted").
 /// </summary>
        public static S7StaticsStatus TryDecode(byte* payload, int bytes, out BatchStaticsHeader header, StaticPrimitive[] dst, out int count)
        {
            header = default(BatchStaticsHeader);
            count = 0;
            if (payload == null || bytes < HeaderBytes) return S7StaticsStatus.TooShort;
            header = *(BatchStaticsHeader*)payload;
            if (header.count > (uint)S7Conventions.StaticsLimits.MaxPerBatch) return S7StaticsStatus.TooMany;
            int n = (int)header.count;
            if (bytes != PayloadBytes(n)) return S7StaticsStatus.BadCount;
            if (*(ushort*)(payload + VersionOffset) != (ushort)S7Conventions.C12Ext.Version) return S7StaticsStatus.BadVersion;
            if (dst == null || dst.Length < n) return S7StaticsStatus.BufferTooSmall;
            for (int i = 0; i < n; i++)
            {
                StaticPrimitive p = *(StaticPrimitive*)(payload + HeaderBytes + i * PrimitiveBytes);
                S7StaticsStatus st = Check(ref p);
                if (st != S7StaticsStatus.Ok) return st;
                dst[i] = p;
            }
            count = n;
            return S7StaticsStatus.Ok;
        }

        public static S7StaticsStatus TryDecode(byte[] payload, int bytes, out BatchStaticsHeader header, StaticPrimitive[] dst, out int count)
        {
            if (payload == null || bytes < 0 || bytes > payload.Length) { header = default(BatchStaticsHeader); count = 0; return S7StaticsStatus.TooShort; }
            fixed (byte* p = payload) return TryDecode(p, bytes, out header, dst, out count);
        }

        /// <summary>The u16 at the header pad 0x06 (0 = an encoder before S7; 1 = S7).</summary>
        public static ushort ReadVersion(byte* payload) { return *(ushort*)(payload + VersionOffset); }

        /// <summary>One primitive's checks (kind, finite numbers, unit rotation, sizes, capsule axis).</summary>
        public static S7StaticsStatus Check(ref StaticPrimitive p)
        {
            if (p.kind < StaticKind.Box || p.kind > StaticKind.MeshObb) return S7StaticsStatus.BadKind;
            if (!Finite(p.centreSn.x) || !Finite(p.centreSn.y) || !Finite(p.centreSn.z) ||
                !Finite(p.qSn.x) || !Finite(p.qSn.y) || !Finite(p.qSn.z) || !Finite(p.qSn.w) ||
                !Finite(p.size.x) || !Finite(p.size.y) || !Finite(p.size.z)) return S7StaticsStatus.NonFinite;
            double ql = Math.Sqrt((double)p.qSn.x * p.qSn.x + (double)p.qSn.y * p.qSn.y + (double)p.qSn.z * p.qSn.z + (double)p.qSn.w * p.qSn.w);
            if (Math.Abs(ql - 1.0) > 1e-3) return S7StaticsStatus.BadRotation;
            double max = S7StaticsMath.MaxHalfExtentM;
            switch (p.kind)
            {
                case StaticKind.Box:
                case StaticKind.MeshObb:
                    if (!(p.size.x > 0) || !(p.size.y > 0) || !(p.size.z > 0) || p.size.x > max || p.size.y > max || p.size.z > max) return S7StaticsStatus.BadSize;
                    return S7StaticsStatus.Ok;
                case StaticKind.Capsule:
                    if (!(p.size.x > 0) || p.size.x > max || !(p.size.y >= 0) || p.size.y > max) return S7StaticsStatus.BadSize;
                    if (!(p.size.z == 0f || p.size.z == 1f || p.size.z == 2f)) return S7StaticsStatus.BadAxis;
                    return S7StaticsStatus.Ok;
                default: // Sphere
                    if (!(p.size.x > 0) || p.size.x > max) return S7StaticsStatus.BadSize;
                    return S7StaticsStatus.Ok;
            }
        }

        /// <summary>One BatchClear payload (16 B; the header pad 0x06 stays 0: the S7 version is the BatchStatics header's).</summary>
        public static int EncodeClear(int bx, int by, int bz, uint epoch, byte[] dst)
        {
            if (dst == null || dst.Length < ClearBytes) throw new ArgumentException("destination smaller than 16 bytes");
            CheckBatchIndex(bx, "bx"); CheckBatchIndex(by, "by"); CheckBatchIndex(bz, "bz");
            var c = new BatchClear { bx = (short)bx, by = (short)by, bz = (short)bz, epoch = epoch };
            fixed (byte* p = dst) *(BatchClear*)p = c;
            return ClearBytes;
        }

        public static bool TryDecodeClear(byte* payload, int bytes, out BatchClear clear)
        {
            clear = default(BatchClear);
            if (payload == null || bytes != ClearBytes) return false;
            clear = *(BatchClear*)payload;
            return true;
        }

        public static string StatusName(S7StaticsStatus s)
        {
            switch (s)
            {
                case S7StaticsStatus.Ok: return "ok";
                case S7StaticsStatus.TooShort: return "tooShort";
                case S7StaticsStatus.BadCount: return "badCount";
                case S7StaticsStatus.TooMany: return "tooMany";
                case S7StaticsStatus.BadVersion: return "badVersion";
                case S7StaticsStatus.BadKind: return "badKind";
                case S7StaticsStatus.NonFinite: return "nonFinite";
                case S7StaticsStatus.BadSize: return "badSize";
                case S7StaticsStatus.BadAxis: return "badAxis";
                case S7StaticsStatus.BadRotation: return "badRotation";
                case S7StaticsStatus.BufferTooSmall: return "bufferTooSmall";
                default: return "status" + ((int)s).ToString(CultureInfo.InvariantCulture);
            }
        }

        private static bool Finite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }

        private static void CheckBatchIndex(int v, string name)
        {
            if (v < short.MinValue || v > short.MaxValue) throw new ArgumentOutOfRangeException(name, "an i16 batch index");
        }
    }

    /// <summary>A Subnautica static primitive mapped to SE world (doubles): the shape the statics host adds.</summary>
    public struct S7StaticSe
    {
        public byte Kind;
        /// <summary>False when the primitive is dropped (determinant check, degenerate data); <see cref="Drop"/> says why.</summary>
        public bool Ok;
        public string Drop;
        /// <summary>SE world centre F⁻¹(c) (the capsule's: the mid point of its end points).</summary>
        public Vec3 Centre;
        /// <summary>Box / MeshObb: rows staticAxes(q), translation = <see cref="Centre"/>.</summary>
        public SeMatrix Axes;
        /// <summary>Box / MeshObb half extents in SE metres.</summary>
        public Vec3 Half;
        /// <summary>Capsule end points (SE world): F⁻¹(c − a·h), F⁻¹(c + a·h).</summary>
        public Vec3 A, B;
        /// <summary>Capsule / sphere radius in SE metres (r / s(c)).</summary>
        public double Radius;
        /// <summary>s = Rs / |F⁻¹(c) − C| at the centre.</summary>
        public double S;
        /// <summary>Determinant of the box rows (+1 expected).</summary>
        public double Determinant;

        /// <summary>A sphere around the shape (SE metres): the host's reach and the batch's bounds.</summary>
        public double BoundingRadius
        {
            get
            {
                switch (Kind)
                {
                    case StaticKind.Capsule: return 0.5 * (B - A).Length + Radius;
                    case StaticKind.Sphere: return Radius;
                    default: return Half.Length;
                }
            }
        }
    }

    /// <summary>Why a collider was not reported (the Subnautica <c>s7.statics</c> row's <c>filtered{reason: n}</c>).</summary>
    public enum S7StaticsReject : byte
    {
        None = 0,
        Disabled = 1,
        Trigger = 2,
        /// <summary>Not on the Default layer (the overlap query already filters it; kept for direct callers).</summary>
        Layer = 3,
        /// <summary>One of our objects: an S6 proxy (<c>ProxyMarker</c>) in the hierarchy.</summary>
        Proxy = 4,
        /// <summary>One of our objects: the bridge's own roots (mirror frames, ghost, self-tests: "DE."/"DeepEngineers.").</summary>
        Bridge = 5,
        /// <summary>Neither under a <c>LargeWorldEntity</c> nor under a batch root object.</summary>
        NotWorld = 6,
        Creature = 7,
        Pickupable = 8,
        Vehicle = 9,
        /// <summary><c>FreezeRigidbodyWhenFar</c> in the hierarchy (a frozen reaper is kinematic by state, never static).</summary>
        Freeze = 10,
        WorldForces = 11,
        /// <summary>A Rigidbody and the root's class id is not on the census allow-list.</summary>
        Rigidbody = 12,
        /// <summary>A LiveMixin and the root's class id is not on the census allow-list.</summary>
        LiveMixin = 13,
        /// <summary>Seeded failure S7.M3: the reporter drops capsules.</summary>
        MutantCapsule = 14,
        /// <summary>The primitive lies entirely above SN y = 0 (not sent).</summary>
        AboveSea = 15,
        /// <summary>Degenerate geometry (no mesh, zero or huge size, NaN).</summary>
        Degenerate = 16,
        /// <summary>Already in the batch set.</summary>
        Duplicate = 17,
        /// <summary>The batch set is full (<c>MaxPerBatch</c>); the rest is logged.</summary>
        Full = 18,
    }

    /// <summary>What the Subnautica reporter knows about one collider and its hierarchy (Unity-free).</summary>
    public struct S7StaticsCandidate
    {
        public bool Enabled, Trigger;
        public int Layer;
        /// <summary>Under a <c>LargeWorldEntity</c>, or under a batch root object (<c>LargeWorldBatchRoot</c>).</summary>
        public bool UnderWorld;
        public bool Proxy, Bridge;
        public bool Creature, Pickupable, Vehicle, Freeze, WorldForces;
        public bool Rigidbody, RigidbodyKinematic, LiveMixin;
        /// <summary>The root's <c>UniqueIdentifier.ClassId</c> is on the census allow-list.</summary>
        public bool AllowListed;
        /// <summary>
 /// The root's class id is on the census list <c>allowWorldForces</c>: a prefab whose
 /// Rigidbody is serialized <c>isKinematic</c> and whose class has no Creature, Pickupable, Vehicle or
 /// FreezeRigidbodyWhenFar (the five explorable wrecks and the exploded Aurora debris carry a WorldForces script on
 /// a kinematic body). Waives <c>WorldForces</c> only while the body is kinematic at run time.
 /// </summary>
        public bool AllowListedWorldForces;
    }

    /// <summary>
 /// The statics filter (CONTRACT §5.4 "C12 static primitives (S7)"): a collider counts when enabled, non-trigger, on
 /// the Default layer, and its hierarchy passes: under a <c>LargeWorldEntity</c> (or a batch root object), no
 /// <c>Creature</c>, <c>Pickupable</c>, <c>Vehicle</c>, <c>FreezeRigidbodyWhenFar</c>, <c>WorldForces</c>, S6
 /// <c>ProxyMarker</c>, the bridge root, and no <c>Rigidbody</c> or <c>LiveMixin</c> unless the root's class id is on
 /// the census allow-list: chosen by entity type, never by "static or kinematic" (seeded failure S7.M7 does exactly
 /// that and reports a frozen reaper).
 /// <para>S7 review fixes: the layer test takes the reporter's mask (F5: every layer Subnautica's player
 /// collides with, minus TerrainCollider and Trigger: <c>LayerID.GetAllCollisionsMask</c>'s pattern; the Default-only
 /// mask stays the default of the two-argument overload), and <c>WorldForces</c> is waived for a class on the census
 /// list <c>allowWorldForces</c> while its body is kinematic (F3).</para>
 /// </summary>
    public static class S7StaticsFilter
    {
        /// <summary>Unity's Default layer (the overlap mask is <c>1 &lt;&lt; 0</c>).</summary>
        public const int DefaultLayer = 0;
        /// <summary>The Default-only layer mask (the contract's first mask; the two-argument <see cref="Decide(ref S7StaticsCandidate, bool)"/>).</summary>
        public const int DefaultMask = 1 << DefaultLayer;

        /// <param name="c">the collider and its hierarchy</param>
 /// <param name="staticOrKinematic">S7.M7: entity-type checks replaced by "no Rigidbody, or a kinematic one"</param>
        public static S7StaticsReject Decide(ref S7StaticsCandidate c, bool staticOrKinematic)
        {
            return Decide(ref c, staticOrKinematic, DefaultMask);
        }

        /// <param name="c">the collider and its hierarchy</param>
 /// <param name="staticOrKinematic">S7.M7: entity-type checks replaced by "no Rigidbody, or a kinematic one"</param>
 /// <param name="layerMask">the layers the reporter scans (<see cref="PlayerSolidMask"/>; bit l = layer l)</param>
        public static S7StaticsReject Decide(ref S7StaticsCandidate c, bool staticOrKinematic, int layerMask)
        {
            if (!c.Enabled) return S7StaticsReject.Disabled;
            if (c.Trigger) return S7StaticsReject.Trigger;
            if (c.Layer < 0 || c.Layer > 31 || (layerMask & (1 << c.Layer)) == 0) return S7StaticsReject.Layer;
            if (c.Proxy) return S7StaticsReject.Proxy;
            if (c.Bridge) return S7StaticsReject.Bridge;
            if (!c.UnderWorld) return S7StaticsReject.NotWorld;
            if (staticOrKinematic)
                return c.Rigidbody && !c.RigidbodyKinematic ? S7StaticsReject.Rigidbody : S7StaticsReject.None;
            if (c.Creature) return S7StaticsReject.Creature;
            if (c.Pickupable) return S7StaticsReject.Pickupable;
            if (c.Vehicle) return S7StaticsReject.Vehicle;
            if (c.Freeze) return S7StaticsReject.Freeze;
            // F3: a kinematic wreck with a WorldForces script, on the census list allowWorldForces (Creature, Pickupable,
 // Vehicle and Freeze were refused above, so a frozen reaper never gets here)
            if (c.WorldForces && !(c.AllowListedWorldForces && c.Rigidbody && c.RigidbodyKinematic)) return S7StaticsReject.WorldForces;
            if (c.Rigidbody && !c.AllowListed) return S7StaticsReject.Rigidbody;
            if (c.LiveMixin && !c.AllowListed) return S7StaticsReject.LiveMixin;
            return S7StaticsReject.None;
        }

        /// <summary>
 /// The primitive kinds the reporter sends: every kind, except capsules under the seeded failure S7.M3 ("the
 /// statics reporter drops capsules": Lost River trees and skeletons lose their capsules, S7.N5 rays pass through).
 /// </summary>
        public static S7StaticsReject KindAllowed(byte kind, bool dropCapsules)
        {
            return dropCapsules && kind == StaticKind.Capsule ? S7StaticsReject.MutantCapsule : S7StaticsReject.None;
        }

        /// <summary>
 /// The reporter's layer mask: every layer l the player collides with
 /// (<paramref name="playerCollides"/>(l) = <c>!Physics.GetIgnoreLayerCollision(LayerID.Player, l)</c>, the pattern of
 /// <c>LayerID.GetAllCollisionsMask</c>), minus <paramref name="terrainLayer"/> (the S4 octree path owns terrain) and
 /// <paramref name="triggerLayer"/>; a negative layer id is ignored. The Default layer is always in (the contract's
 /// first mask).m_LayerCollisionMatrix, read-only): the player
 /// collides with every layer but OnlyVehicle (9), so the wreck walls on DiveReel (26) are in.
 /// </summary>
        public static int PlayerSolidMask(Func<int, bool> playerCollides, int terrainLayer, int triggerLayer)
        {
            int mask = DefaultMask;
            for (int l = 0; l < 32; l++)
            {
                if (l == terrainLayer || l == triggerLayer) continue;
                if (playerCollides != null && playerCollides(l)) mask |= 1 << l;
            }
            return mask;
        }

        public static string ReasonName(S7StaticsReject r)
        {
            switch (r)
            {
                case S7StaticsReject.None: return "none";
                case S7StaticsReject.Disabled: return "disabled";
                case S7StaticsReject.Trigger: return "trigger";
                case S7StaticsReject.Layer: return "layer";
                case S7StaticsReject.Proxy: return "proxy";
                case S7StaticsReject.Bridge: return "bridge";
                case S7StaticsReject.NotWorld: return "notWorld";
                case S7StaticsReject.Creature: return "creature";
                case S7StaticsReject.Pickupable: return "pickupable";
                case S7StaticsReject.Vehicle: return "vehicle";
                case S7StaticsReject.Freeze: return "freezeWhenFar";
                case S7StaticsReject.WorldForces: return "worldForces";
                case S7StaticsReject.Rigidbody: return "rigidbody";
                case S7StaticsReject.LiveMixin: return "liveMixin";
                case S7StaticsReject.MutantCapsule: return "mutantM3capsule";
                case S7StaticsReject.AboveSea: return "aboveSea";
                case S7StaticsReject.Degenerate: return "degenerate";
                case S7StaticsReject.Duplicate: return "duplicate";
                case S7StaticsReject.Full: return "full";
                default: return "reason" + ((int)r).ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Number of reasons (for counter arrays indexed by <see cref="S7StaticsReject"/>).</summary>
        public const int ReasonCount = 19;
    }

    /// <summary>
 /// The geometry and naming rules of the statics path (CONTRACT §2.3, §5.4 "S7 conventions" <c>format statics.*</c>).
 /// </summary>
    public static class S7StaticsMath
    {
        /// <summary>
 /// Subnautica's <c>LargeWorldStreamer</c> land origin (SN world of batch (0, 0, 0)'s minimum corner; the S4
 /// octree origin, <c>SnOctreeFormat.Origin*</c>, VERIFIED from the game's data in S4) and the batch lattice.
 /// </summary>
        public static readonly Vec3 LandOriginSn = new Vec3(-2048.0, -3040.0, -2048.0);
        public const int BatchCountX = 26, BatchCountY = 20, BatchCountZ = 26;

        /// <summary>The prefix of every statics host name (<c>format statics.host</c>).</summary>
        public const string HostPrefix = "DE.Statics.";
        /// <summary>Census cache files: <c>%LOCALAPPDATA%\DeepEngineers\census\batches\&lt;bx&gt;.&lt;by&gt;.&lt;bz&gt;.c12</c>.</summary>
        public const string CensusExtension = ".c12";

        /// <summary>Smallest half extent / radius the reporter sends (a flat Unity box is clamped to 5 mm).</summary>
        public const double MinHalfExtentM = 0.005;
        /// <summary>Largest half extent / radius a message may carry (half the Subnautica tile).</summary>
        public const double MaxHalfExtentM = 2048.0;
        /// <summary><c>SkewApprox</c>: some pair of the world matrix's columns is more than 1 % from orthogonal.</summary>
        public const double ShearTolerance = 0.01;
        /// <summary>The box rows' determinant must be +1 within this (else the primitive is dropped and counted).</summary>
        public const double DeterminantTolerance = 1e-3;

        // ------------------------------------------------------------------------------------------- batches

        public static Vec3 BatchMinsSn(int bx, int by, int bz)
        {
            double m = S7Conventions.StaticsLimits.BatchM;
            return new Vec3(LandOriginSn.X + bx * m, LandOriginSn.Y + by * m, LandOriginSn.Z + bz * m);
        }

        public static Vec3 BatchCentreSn(int bx, int by, int bz)
        {
            double h = 0.5 * S7Conventions.StaticsLimits.BatchM;
            Vec3 m = BatchMinsSn(bx, by, bz);
            return new Vec3(m.X + h, m.Y + h, m.Z + h);
        }

        /// <summary>The batch containing an SN point: floor((p − origin) / 160) per axis (<c>GetContainingBatch</c>).</summary>
        public static void BatchOfSn(Vec3 p, out int bx, out int by, out int bz)
        {
            double m = S7Conventions.StaticsLimits.BatchM;
            bx = (int)Math.Floor((p.X - LandOriginSn.X) / m);
            by = (int)Math.Floor((p.Y - LandOriginSn.Y) / m);
            bz = (int)Math.Floor((p.Z - LandOriginSn.Z) / m);
        }

        /// <summary>Distance from an SN point to a batch's box (0 inside).</summary>
        public static double DistanceToBatchSn(Vec3 p, int bx, int by, int bz)
        {
            Vec3 lo = BatchMinsSn(bx, by, bz);
            double m = S7Conventions.StaticsLimits.BatchM;
            double dx = Math.Max(0.0, Math.Max(lo.X - p.X, p.X - (lo.X + m)));
            double dy = Math.Max(0.0, Math.Max(lo.Y - p.Y, p.Y - (lo.Y + m)));
            double dz = Math.Max(0.0, Math.Max(lo.Z - p.Z, p.Z - (lo.Z + m)));
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>Inside Subnautica's batch lattice (26 × 20 × 26).</summary>
        public static bool InLattice(int bx, int by, int bz)
        {
            return bx >= 0 && bx < BatchCountX && by >= 0 && by < BatchCountY && bz >= 0 && bz < BatchCountZ;
        }

        /// <summary>A batch index packed into one key (i16 × 3).</summary>
        public static long BatchKey(int bx, int by, int bz)
        {
            return ((long)(ushort)(short)bx << 32) | ((long)(ushort)(short)by << 16) | (ushort)(short)bz;
        }

        public static void UnpackBatchKey(long key, out int bx, out int by, out int bz)
        {
            bx = (short)(ushort)((key >> 32) & 0xFFFF);
            by = (short)(ushort)((key >> 16) & 0xFFFF);
            bz = (short)(ushort)(key & 0xFFFF);
        }

        // ------------------------------------------------------------------------------------------- ids and keys

 /// <summary>FNV-1a 32 (offset basis 0x811c9dc5, prime 0x01000193) of the class id's UTF-8 bytes; 0 for null.</summary>
        public static uint ClassIdHash(string classId)
        {
            if (classId == null) return 0;
            byte[] b = Encoding.UTF8.GetBytes(classId);
            uint h = 0x811c9dc5u;
            for (int i = 0; i < b.Length; i++) { h ^= b[i]; h *= 0x01000193u; }
            return h;
        }

        /// <summary>
 /// The dedup key of a primitive within a batch: (kind, centre quantised
 /// to 1 cm, size quantised to 1 cm, rotation quantised to 1/1000, classIdHash) as FNV-1a 64 of {u8 kind,
 /// i32 round(c·100) ×3, i32 round(size·100) ×3, i32 round(q·1000) ×4 (x, y, z, w), u32 classIdHash} (45 bytes,
 /// little-endian; rounding half away from zero). CONTRACT prose, not a canonical-text line: no pin moves.
 /// </summary>
        public static ulong DedupKey(byte kind, float cx, float cy, float cz, float sx, float sy, float sz, float qx, float qy, float qz, float qw, uint classIdHash)
        {
            ulong h = 0xcbf29ce484222325UL;
            h = Mix(h, kind);
            h = Mix32(h, (uint)Quant(cx));
            h = Mix32(h, (uint)Quant(cy));
            h = Mix32(h, (uint)Quant(cz));
            h = Mix32(h, (uint)Quant(sx));
            h = Mix32(h, (uint)Quant(sy));
            h = Mix32(h, (uint)Quant(sz));
            h = Mix32(h, (uint)QuantRot(qx));
            h = Mix32(h, (uint)QuantRot(qy));
            h = Mix32(h, (uint)QuantRot(qz));
            h = Mix32(h, (uint)QuantRot(qw));
            h = Mix32(h, classIdHash);
            return h;
        }

        public static ulong DedupKey(ref StaticPrimitive p)
        {
            return DedupKey(p.kind, p.centreSn.x, p.centreSn.y, p.centreSn.z, p.size.x, p.size.y, p.size.z, p.qSn.x, p.qSn.y, p.qSn.z, p.qSn.w, p.classIdHash);
        }

        /// <summary>round(v · 1000) half away from zero, clamped to i32 (the dedup key's rotation components).</summary>
        public static int QuantRot(float v)
        {
            double q = Math.Round((double)v * 1000.0, MidpointRounding.AwayFromZero);
            if (q > int.MaxValue) return int.MaxValue;
            if (q < int.MinValue) return int.MinValue;
            return (int)q;
        }

        /// <summary>round(v · 100) half away from zero, clamped to i32.</summary>
        public static int Quant(float v)
        {
            double q = Math.Round((double)v * 100.0, MidpointRounding.AwayFromZero);
            if (q > int.MaxValue) return int.MaxValue;
            if (q < int.MinValue) return int.MinValue;
            return (int)q;
        }

        private static ulong Mix(ulong h, byte b) { h ^= b; return h * 0x100000001b3UL; }

        private static ulong Mix32(ulong h, uint v)
        {
            h = Mix(h, (byte)v); h = Mix(h, (byte)(v >> 8)); h = Mix(h, (byte)(v >> 16)); return Mix(h, (byte)(v >> 24));
        }

        // ------------------------------------------------------------------------------------------- Unity collider sizes

 /// <summary>Box half extents: |size/2 · lossyScale| per axis, at least <see cref="MinHalfExtentM"/>.</summary>
        public static Vec3 BoxHalf(Vec3 size, Vec3 lossyScale)
        {
            return new Vec3(Clamp(Math.Abs(0.5 * size.X * lossyScale.X)), Clamp(Math.Abs(0.5 * size.Y * lossyScale.Y)), Clamp(Math.Abs(0.5 * size.Z * lossyScale.Z)));
        }

        /// <summary>A mesh collider's OBB half extents: |extents · lossyScale| (kind MeshObb).</summary>
        public static Vec3 MeshHalf(Vec3 extents, Vec3 lossyScale)
        {
            return new Vec3(Clamp(Math.Abs(extents.X * lossyScale.X)), Clamp(Math.Abs(extents.Y * lossyScale.Y)), Clamp(Math.Abs(extents.Z * lossyScale.Z)));
        }

        /// <summary>
 /// A Unity capsule as the wire's (radius, halfSegment, axis) (<c>format statics.capsule</c>): Unity scales the
 /// radius by the larger of the two scales across the axis and the height (caps included) by the scale along it;
 /// halfSegment = max(0, height/2 − radius).
 /// </summary>
        public static Vec3 CapsuleSize(double radius, double height, int direction, Vec3 lossyScale)
        {
            if (direction < 0 || direction > 2) throw new ArgumentOutOfRangeException("direction", "0, 1 or 2");
            double sx = Math.Abs(lossyScale.X), sy = Math.Abs(lossyScale.Y), sz = Math.Abs(lossyScale.Z);
            double across, along;
            switch (direction)
            {
                case 0: along = sx; across = Math.Max(sy, sz); break;
                case 1: along = sy; across = Math.Max(sx, sz); break;
                default: along = sz; across = Math.Max(sx, sy); break;
            }
            double r = Clamp(Math.Abs(radius) * across);
            double h = Math.Abs(height) * along;
            return new Vec3(r, Math.Max(0.0, 0.5 * h - r), direction);
        }

        /// <summary>Sphere radius × the largest |lossyScale| component.</summary>
        public static double SphereRadius(double radius, Vec3 lossyScale)
        {
            return Clamp(Math.Abs(radius) * Math.Max(Math.Abs(lossyScale.X), Math.Max(Math.Abs(lossyScale.Y), Math.Abs(lossyScale.Z))));
        }

        /// <summary>Largest |cos| between two of the three columns of a world matrix's 3 × 3 part (0 = no shear).</summary>
        public static double Shear(Vec3 c0, Vec3 c1, Vec3 c2)
        {
            double l0 = c0.Length, l1 = c1.Length, l2 = c2.Length;
            if (l0 <= 0 || l1 <= 0 || l2 <= 0) return 1.0;
            double a = Math.Abs(Vec3.Dot(c0, c1)) / (l0 * l1);
            double b = Math.Abs(Vec3.Dot(c0, c2)) / (l0 * l2);
            double c = Math.Abs(Vec3.Dot(c1, c2)) / (l1 * l2);
            return Math.Max(a, Math.Max(b, c));
        }

        /// <summary><c>SkewApprox</c>: a non-uniform scale under a rotation, more than 1 % shear.</summary>
        public static bool IsSkewed(Vec3 c0, Vec3 c1, Vec3 c2) { return Shear(c0, c1, c2) > ShearTolerance; }

        private static double Clamp(double v) { return v < MinHalfExtentM ? MinHalfExtentM : v; }

        /// <summary>q with w ≥ 0, normalised (the wire convention).</summary>
        public static Quat WireRotation(Quat q)
        {
            return q.Normalized().WithNonNegativeW();
        }

        // ------------------------------------------------------------------------------------------- the sea

 /// <summary>The lowest SN y of a primitive (box/MeshObb: c.y − Σ|R_y,i|·h_i; capsule: c.y − |a.y|·h − r; sphere: c.y − r).</summary>
        public static double LowestY(ref StaticPrimitive p)
        {
            var q = new Quat(p.qSn.x, p.qSn.y, p.qSn.z, p.qSn.w).Normalized();
            double cy = p.centreSn.y;
            switch (p.kind)
            {
                case StaticKind.Capsule:
                {
                    Vec3 a = q.Rotate(Axis((int)p.size.z));
                    return cy - Math.Abs(a.Y) * p.size.y - p.size.x;
                }
                case StaticKind.Sphere:
                    return cy - p.size.x;
                default:
                {
                    Vec3 ax = q.Rotate(Vec3.UnitX), ay = q.Rotate(Vec3.UnitY), az = q.Rotate(Vec3.UnitZ);
                    return cy - (Math.Abs(ax.Y) * p.size.x + Math.Abs(ay.Y) * p.size.y + Math.Abs(az.Y) * p.size.z);
                }
            }
        }

        /// <summary>Primitives entirely above SN y = 0 are not sent.</summary>
        public static bool EntirelyAboveSea(ref StaticPrimitive p) { return LowestY(ref p) > 0.0; }

        /// <summary>The unit vector of a capsule axis index (0 x, 1 y, 2 z).</summary>
        public static Vec3 Axis(int axis)
        {
            switch (axis)
            {
                case 0: return Vec3.UnitX;
                case 1: return Vec3.UnitY;
                default: return Vec3.UnitZ;
            }
        }

        // ------------------------------------------------------------------------------------------- SE geometry

 /// <summary>
 /// One primitive in SE world (<c>format statics.axes</c>, <c>statics.capsule</c>): box and MeshObb through
 /// <see cref="WireMath.StaticToSe"/> (centre F⁻¹(c), rows staticAxes(q), half_i·|S⁻¹·(q·e_i)|), determinant +1
 /// asserted; capsule end points F⁻¹(c ∓ a·h) exactly with a = q·ê_axis, radius r/s(c); sphere centre F⁻¹(c),
 /// radius r/s(c).
 /// </summary>
        public static S7StaticSe ToSe(SiteMap site, ref StaticPrimitive p)
        {
            if (site == null) throw new ArgumentNullException("site");
            var r = new S7StaticSe { Kind = p.kind, Ok = true, Determinant = 1.0 };
            var c = new Vec3(p.centreSn.x, p.centreSn.y, p.centreSn.z);
            var q = new Quat(p.qSn.x, p.qSn.y, p.qSn.z, p.qSn.w);
            double ql = q.Length;
            if (!(ql > 0.5) || !c.IsFinite) { r.Ok = false; r.Drop = "rotation"; return r; }
            Quat qn = q.Normalized();
            switch (p.kind)
            {
                case StaticKind.Box:
                case StaticKind.MeshObb:
                {
                    StaticBoxSe b = WireMath.StaticToSe(site, c, qn, new Vec3(p.size.x, p.size.y, p.size.z));
                    r.Centre = b.Centre; r.Axes = b.Matrix; r.Half = b.HalfExtents; r.S = b.S; r.Determinant = b.Determinant;
                    if (Math.Abs(b.Determinant - 1.0) > DeterminantTolerance) { r.Ok = false; r.Drop = "determinant"; }
                    return r;
                }
                case StaticKind.Capsule:
                {
                    Vec3 a = qn.Rotate(Axis((int)p.size.z));
                    double h = p.size.y;
                    r.A = site.SnToSe(c - a * h);
                    r.B = site.SnToSe(c + a * h);
                    Vec3 ce = site.SnToSe(c);
                    r.S = site.Rs / (ce - site.C).Length;
                    r.Centre = 0.5 * (r.A + r.B);
                    r.Radius = p.size.x / r.S;
                    return r;
                }
                case StaticKind.Sphere:
                {
                    r.Centre = site.SnToSe(c);
                    r.S = site.Rs / (r.Centre - site.C).Length;
                    r.Radius = p.size.x / r.S;
                    return r;
                }
                default:
                    r.Ok = false; r.Drop = "kind"; return r;
            }
        }

        // ------------------------------------------------------------------------------------------- hosts

 /// <summary>
 /// <c>DE.Statics.&lt;bx&gt;.&lt;by&gt;.&lt;bz&gt;.e&lt;epoch&gt;[.p&lt;k&gt;]</c>: <paramref name="part"/> &lt; 0 for a batch
 /// held by one host (no suffix), else the host's index k when the batch has more than <c>MaxPerHost</c> primitives.
 /// </summary>
        public static string HostName(int bx, int by, int bz, uint epoch, int part)
        {
            var sb = new StringBuilder(40);
            sb.Append(HostPrefix).Append(bx.ToString(CultureInfo.InvariantCulture)).Append('.').Append(by.ToString(CultureInfo.InvariantCulture))
              .Append('.').Append(bz.ToString(CultureInfo.InvariantCulture)).Append(".e").Append(epoch.ToString(CultureInfo.InvariantCulture));
            if (part >= 0) sb.Append(".p").Append(part.ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        public static bool IsHostName(string name)
        {
            return name != null && name.StartsWith(HostPrefix, StringComparison.Ordinal);
        }

        public static bool TryParseHostName(string name, out int bx, out int by, out int bz, out uint epoch, out int part)
        {
            bx = by = bz = 0; epoch = 0; part = -1;
            if (!IsHostName(name)) return false;
            string[] t = name.Substring(HostPrefix.Length).Split('.');
            if (t.Length != 4 && t.Length != 5) return false;
            if (!int.TryParse(t[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out bx)) return false;
            if (!int.TryParse(t[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out by)) return false;
            if (!int.TryParse(t[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out bz)) return false;
            if (t[3].Length < 2 || t[3][0] != 'e' || !uint.TryParse(t[3].Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out epoch)) return false;
            if (t.Length == 5)
            {
                if (t[4].Length < 2 || t[4][0] != 'p' || !int.TryParse(t[4].Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out part)) return false;
            }
            return true;
        }

        /// <summary>Hosts a batch of <paramref name="count"/> primitives needs: ⌈count / MaxPerHost⌉, at least 1.</summary>
        public static int HostParts(int count)
        {
            int m = S7Conventions.StaticsLimits.MaxPerHost;
            return count <= m ? 1 : (count + m - 1) / m;
        }

        /// <summary>The primitives of host <paramref name="part"/> (0-based) of a batch of <paramref name="count"/>.</summary>
        public static void HostRange(int part, int count, out int start, out int n)
        {
            int m = S7Conventions.StaticsLimits.MaxPerHost;
            start = part * m;
            n = Math.Max(0, Math.Min(m, count - start));
        }

        /// <summary>
 /// The host life cycle: created while a mirrored grid or a character is within <c>CreateRadiusM</c> (300 m) of the
 /// batch's primitives, closed beyond <c>CloseRadiusM</c> (350 m); in between the current state holds.
 /// </summary>
        public static bool HostWanted(bool exists, double distanceM)
        {
            if (double.IsNaN(distanceM)) return false;
            return exists ? distanceM <= S7Conventions.StaticsLimits.CloseRadiusM : distanceM <= S7Conventions.StaticsLimits.CreateRadiusM;
        }

        /// <summary>
 /// Which source of a batch wins on SE: a live message replaces a census one; a census file never replaces a live
 /// set; within one source the newer epoch wins (per Subnautica generation).
 /// </summary>
        public static bool Supersedes(bool haveCurrent, bool currentCensus, uint currentEpoch, bool incomingCensus, uint incomingEpoch)
        {
            if (!haveCurrent) return true;
            if (currentCensus != incomingCensus) return !incomingCensus;
            return incomingEpoch > currentEpoch;
        }

        // ------------------------------------------------------------------------------------------- census files

        public static string CensusFileName(int bx, int by, int bz)
        {
            return bx.ToString(CultureInfo.InvariantCulture) + "." + by.ToString(CultureInfo.InvariantCulture) + "." + bz.ToString(CultureInfo.InvariantCulture) + CensusExtension;
        }

        public static bool TryParseCensusFileName(string fileName, out int bx, out int by, out int bz)
        {
            bx = by = bz = 0;
            if (fileName == null || !fileName.EndsWith(CensusExtension, StringComparison.OrdinalIgnoreCase)) return false;
            string[] t = fileName.Substring(0, fileName.Length - CensusExtension.Length).Split('.');
            return t.Length == 3 &&
                   int.TryParse(t[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out bx) &&
                   int.TryParse(t[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out by) &&
                   int.TryParse(t[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out bz);
        }

        /// <summary>
 /// The census folders under a <c>%LOCALAPPDATA%</c> root: <c>DeepEngineers\census</c> (the allow-list) and its
 /// <c>batches</c> subfolder (the.c12 files). Derived data, never shipped.
 /// </summary>
        public static string CensusFolder(string localAppData)
        {
            return System.IO.Path.Combine(System.IO.Path.Combine(localAppData ?? "", "DeepEngineers"), "census");
        }

        public static string CensusBatchesFolder(string localAppData)
        {
            return System.IO.Path.Combine(CensusFolder(localAppData), "batches");
        }

        /// <summary>The allow-list file the census tool writes (<c>tools/sn-octrees/sn_prefab_colliders.py --census</c>).</summary>
        public const string AllowListFileName = "allowlist.json";

        /// <summary>
 /// The root name Subnautica gives a lightmapped prefab scene's main object once loaded (<c>LightmappedPrefabs.Update</c>:
 /// <c>gameObject.name = loadingScene + "-MainPrefab"</c>, parent null; the Aurora is one). Such a root is neither under
 /// a <c>LargeWorldEntity</c> nor under a batch root, so the reporter accepts it as world explicitly.
 /// </summary>
        public const string LightmappedSuffix = "-MainPrefab";

        /// <summary>
 /// The census/allow-list class id of a lightmapped prefab scene root: <c>scene:&lt;scene name, lower case&gt;</c>
 /// (the census tool derives the same id from the scene bundle <c>&lt;scene&gt;.unity_*.bundle</c>); null when the
 /// name is not such a root.
 /// </summary>
        public static string LightmappedSceneClassId(string rootName)
        {
            if (rootName == null || rootName.Length <= LightmappedSuffix.Length || !rootName.EndsWith(LightmappedSuffix, StringComparison.Ordinal)) return null;
            return "scene:" + rootName.Substring(0, rootName.Length - LightmappedSuffix.Length).ToLowerInvariant();
        }
    }

    /// <summary>Outcome of <see cref="S7StaticsBatchSet.Add"/>.</summary>
    public enum S7StaticsAdd : byte { Added = 0, Duplicate = 1, Full = 2 }

    /// <summary>
 /// One batch's primitives on the Subnautica side: a set that only grows within one Subnautica generation and world
 /// epoch (a cell unload never removes a wreck), deduplicated by (kind, centre and size to 1 cm, rotation to 1/1000,
 /// classIdHash; <see cref="S7StaticsMath.DedupKey(ref StaticPrimitive)"/>, review F16), at most
 /// <c>MaxPerBatch</c>; resent with epoch + 1 when new primitives appeared.
 /// </summary>
    public sealed class S7StaticsBatchSet
    {
        public readonly int Bx, By, Bz;
        private StaticPrimitive[] _items = new StaticPrimitive[32];
        private readonly HashSet<ulong> _keys = new HashSet<ulong>();

        public S7StaticsBatchSet(int bx, int by, int bz) { Bx = bx; By = by; Bz = bz; }

        /// <summary>Primitives held.</summary>
        public int Count { get; private set; }
        /// <summary>Epoch of the last message taken (0 = never sent).</summary>
        public uint Epoch { get; private set; }
        /// <summary>New primitives since the last message.</summary>
        public bool Dirty { get; private set; }
        /// <summary>Primitives refused because the set was full (logged).</summary>
        public long Overflow { get; private set; }
        public long Duplicates { get; private set; }
        /// <summary>The primitives (the first <see cref="Count"/> entries are valid).</summary>
        public StaticPrimitive[] Items { get { return _items; } }

        public S7StaticsAdd Add(ref StaticPrimitive p)
        {
            ulong key = S7StaticsMath.DedupKey(ref p);
            if (_keys.Contains(key)) { Duplicates++; return S7StaticsAdd.Duplicate; }
            if (Count >= S7Conventions.StaticsLimits.MaxPerBatch) { Overflow++; return S7StaticsAdd.Full; }
            _keys.Add(key);
            if (Count == _items.Length) Array.Resize(ref _items, Math.Min(S7Conventions.StaticsLimits.MaxPerBatch, _items.Length * 2));
            _items[Count++] = p;
            Dirty = true;
            return S7StaticsAdd.Added;
        }

        /// <summary>Starts the next message: epoch + 1, the dirty mark cleared.</summary>
        public uint NextEpoch()
        {
            Epoch++;
            Dirty = false;
            return Epoch;
        }

        /// <summary>A message whose write failed (ring full) stays due: the dirty mark is set again, the epoch kept.</summary>
        public void Requeue() { Dirty = true; }

        /// <summary>The payload of the current epoch (<paramref name="census"/>: <c>Census</c> set on every primitive).</summary>
        public unsafe int Encode(byte[] dst, bool census)
        {
            if (dst == null) throw new ArgumentNullException("dst");
            fixed (byte* p = dst)
                return S7StaticsCodec.Encode(Bx, By, Bz, Epoch, _items, 0, Count, p, dst.Length, census ? S7Conventions.StaticFlags.Census : (byte)0);
        }
    }
}
