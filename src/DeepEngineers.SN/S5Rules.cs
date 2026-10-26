using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;

namespace DeepEngineers.SN
{
    // =================================================================================================================
 // S5 "whole grids + character", Subnautica side, Unity-free part 1 of 2 (compiled into DeepEngineers.SN.Tests):
 // the S5 mirror conventions as this plugin reads them (CONTRACT §5.4 "C1–C4 conventions for S5": the version
 // nibble, part classes and flag bits, the canonical skeleton canon1), the fpbody switch, the S5 halves of the perf
 // recipes' seeded failures (S5.M4/M5), the chunk build gate (drain reached its head, one build in flight per cell,
 // ≤ 256 queued, dirty-newest then nearest), frame statistics and the s5.* JSONL row helpers. The Unity halves are
 // S5Mirror.cs, CharacterMirror.cs and S5Host.cs; the math is S5Math.cs.
 // =================================================================================================================

 /// <summary>
 /// The S5 mirror conventions (CONTRACT §5.4 "C1–C4 conventions for S5", conventions hash <c>0x31dfd6f9befdef5d</c>).
 /// The values are this plugin's own copy, pinned by S5Tests against the contract's canonical text (byte for byte,
 /// FNV-1a 64): the Wire class <c>MirrorConventions</c> carries the same text, so the two cannot drift apart unnoticed.
 /// </summary>
    internal static class S5Conv
    {
        public const string ExtLine = "ext s5 mirror 1";
        public const ulong ConventionsHash = 0x31dfd6f9befdef5dUL;
        public const ulong SkeletonHash = 0x995765282e7d4f1dUL;

        // The values are Wire's; this plugin reads them through these
 // short names. S5Tests pins them against the contract's canonical text independently of Wire.
 // MirrorCharacterFlags
        public const uint CharPlayer = MirrorCharacterFlags.Player, CharFallback = MirrorCharacterFlags.Fallback, CharVersion1 = MirrorCharacterFlags.Version1;
        // MirrorCharacterPart (partKey of the character's parts)
        public const ulong CharPartBody = MirrorCharacterPart.Body, CharPartHead1st = MirrorCharacterPart.Head1st, CharPartTool = MirrorCharacterPart.Tool;
        // MirrorGridFlags
        public const uint GridStatic = MirrorGridFlags.Static, GridSubgrid = MirrorGridFlags.Subgrid, GridFloatingObject = MirrorGridFlags.FloatingObject,
                          GridVersion1 = MirrorGridFlags.Version1;
        // MirrorLimits
        public const int MaxSkinWeights = MirrorLimits.MaxSkinWeights, CanonicalBones = MirrorLimits.CanonicalBones;
        // MirrorPartClass
        public const uint ClassCubePart = MirrorPartClass.CubePart, ClassEdge = MirrorPartClass.Edge, ClassFatBlock = MirrorPartClass.FatBlock,
                          ClassSubpart = MirrorPartClass.Subpart, ClassCharacterBody = MirrorPartClass.CharacterBody, ClassTool = MirrorPartClass.Tool,
                          ClassFloatingObject = MirrorPartClass.FloatingObject;
        // MirrorPartFlags
        public const uint ClassMask = MirrorPartFlags.ClassMask, Dynamic = MirrorPartFlags.Dynamic, HiddenInFirstPerson = MirrorPartFlags.HiddenInFirstPerson,
                          BuildStage = MirrorPartFlags.BuildStage, Deformed = MirrorPartFlags.Deformed, NoShadows = MirrorPartFlags.NoShadows,
                          MetalnessColorable = MirrorPartFlags.MetalnessColorable, PartVersion1 = MirrorPartFlags.Version1, VersionMask = MirrorPartFlags.VersionMask;
        /// <summary>The flag bits that enter the Subnautica chunk key (CONTRACT §5.4 "Chunk key").</summary>
        public const uint ChunkKeyBits = NoShadows | MetalnessColorable;

        private static readonly string[] FormatLines =
        {
            "format modelKey <path>|lod<n>[|scale=<s>][|skin=<skin>][|char=body|char=head1st]",
            "format partKey.cube fnv1a64(u64 modelKey, f32 tx, f32 ty, f32 tz, f32 packedRotation)",
            "format partKey.edge fnv1a64(u32 2, u64 modelKey, f32 tx, f32 ty, f32 tz, f32 packedRotation)",
            "format partKey.fat fnv1a64(utf8 fat|<minX>,<minY>,<minZ>)",
            "format partKey.subpart fnv1a64(utf8 sub|<fatKey hex16>|<name>[/<name>])",
            "format partKey.floating 1",
            "format skinId fnv1a32(utf8 lower(<skin>)) or 0",
        };

        /// <summary>The 28 <c>const</c> lines from the constants above, in the contract's order (group, then value, then name).</summary>
        public static List<string> ConstLines()
        {
            var rows = new List<KeyValuePair<string, KeyValuePair<long, string>>>();
            Action<string, string, long> add = (g, n, v) => rows.Add(new KeyValuePair<string, KeyValuePair<long, string>>(g, new KeyValuePair<long, string>(v, n)));
            add("MirrorCharacterFlags", "Player", CharPlayer); add("MirrorCharacterFlags", "Fallback", CharFallback); add("MirrorCharacterFlags", "Version1", CharVersion1);
            add("MirrorCharacterPart", "Body", (long)CharPartBody); add("MirrorCharacterPart", "Head1st", (long)CharPartHead1st); add("MirrorCharacterPart", "Tool", (long)CharPartTool);
            add("MirrorGridFlags", "Static", GridStatic); add("MirrorGridFlags", "Subgrid", GridSubgrid); add("MirrorGridFlags", "FloatingObject", GridFloatingObject);
            add("MirrorGridFlags", "Version1", GridVersion1);
            add("MirrorLimits", "MaxSkinWeights", MaxSkinWeights); add("MirrorLimits", "CanonicalBones", CanonicalBones);
            add("MirrorPartClass", "CubePart", ClassCubePart); add("MirrorPartClass", "Edge", ClassEdge); add("MirrorPartClass", "FatBlock", ClassFatBlock);
            add("MirrorPartClass", "Subpart", ClassSubpart); add("MirrorPartClass", "CharacterBody", ClassCharacterBody); add("MirrorPartClass", "Tool", ClassTool);
            add("MirrorPartClass", "FloatingObject", ClassFloatingObject);
            add("MirrorPartFlags", "ClassMask", ClassMask); add("MirrorPartFlags", "Dynamic", Dynamic); add("MirrorPartFlags", "HiddenInFirstPerson", HiddenInFirstPerson);
            add("MirrorPartFlags", "BuildStage", BuildStage); add("MirrorPartFlags", "Deformed", Deformed); add("MirrorPartFlags", "NoShadows", NoShadows);
            add("MirrorPartFlags", "MetalnessColorable", MetalnessColorable); add("MirrorPartFlags", "Version1", PartVersion1); add("MirrorPartFlags", "VersionMask", VersionMask);
            rows.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.Key, b.Key);
                if (c != 0) return c;
                c = a.Value.Key.CompareTo(b.Value.Key);
                return c != 0 ? c : string.CompareOrdinal(a.Value.Value, b.Value.Value);
            });
            var lines = new List<string>(rows.Count);
            foreach (var r in rows) lines.Add("const " + r.Key + " " + r.Value.Value + " " + r.Value.Key.ToString(CultureInfo.InvariantCulture));
            return lines;
        }

        /// <summary>The canonical text as this plugin's constants spell it (ASCII, '\n' after every line).</summary>
        public static string CanonicalText()
        {
            var sb = new StringBuilder(1600);
            sb.Append(ExtLine).Append('\n');
            foreach (string l in ConstLines()) sb.Append(l).Append('\n');
            foreach (string l in FormatLines) sb.Append(l).Append('\n');
            sb.Append("skeleton canon1 ").Append(CanonicalBones.ToString(CultureInfo.InvariantCulture)).Append(" 0x").Append(SkeletonHash.ToString("x16", CultureInfo.InvariantCulture)).Append('\n');
            return sb.ToString();
        }

        /// <summary>The version nibble (bits 28–31) of PartAdd/GridAdd/CharacterAdd flags.</summary>
        public static uint Version(uint flags) { return flags >> 28; }

        /// <summary>FNV-1a 32 of the lower-case skin subtype (offset 0x811c9dc5, prime 0x01000193); 0 without a skin.</summary>
        public static uint SkinId(string skin)
        {
            if (string.IsNullOrEmpty(skin)) return 0;
            uint h = 0x811c9dc5u;
            foreach (byte b in Encoding.UTF8.GetBytes(skin.ToLowerInvariant())) { h ^= b; h *= 0x01000193u; }
            return h;
        }
    }

    /// <summary>How the consumer draws a PartAdd (CONTRACT §5.4 "PartAdd (S5)" and "Chunk key and consumer rules").</summary>
    internal enum PartRole
    {
        /// <summary>Merged into its cell's chunk meshes (classes 1–4 without Dynamic; every version-0 S3 part).</summary>
        Chunk = 0,
        /// <summary>Its own MeshRenderer child with a shared mesh (Dynamic parts, tools, floating objects).</summary>
        Child = 1,
        /// <summary>A character part: a SkinnedMeshRenderer on the canonical bone tree (class 5).</summary>
        Skinned = 2,
        /// <summary>Version nibble other than 0/1, or a class this version does not define: ignored, counted conventionsRejected.</summary>
        Rejected = 3,
    }

    internal static class S5PartRules
    {
        /// <summary>
 /// The role of a PartAdd from its flags (Wire's <see cref="ChunkMath.DrawKind"/>: version 0 = an S3 chunked cube
 /// part; version 1 class 0 = CubePart; classes 8-15 and other nibbles are rejected); <paramref name="cls"/> = the
 /// effective class (<see cref="MirrorConventions.EffectivePartClass"/>).
 /// </summary>
        public static PartRole Role(uint flags, out uint cls)
        {
            cls = MirrorConventions.EffectivePartClass(flags);
            switch (ChunkMath.DrawKind(flags))
            {
                case PartDrawKind.Chunk: return PartRole.Chunk;
                case PartDrawKind.OwnChild:
                case PartDrawKind.Floating: return PartRole.Child;
                case PartDrawKind.Skinned: return PartRole.Skinned;
                default: return PartRole.Rejected;
            }
        }

        /// <summary>The flag bits a chunked part contributes to its chunk key (0 for a version-0 part).</summary>
        public static uint ChunkBits(uint flags) { return ChunkMath.ChunkFlags(flags); }

        /// <summary>GridAdd / CharacterAdd: false when the version nibble is neither 0 nor 1 (ignored, counted).</summary>
        public static bool VersionAccepted(uint flags) { return MirrorConventions.Classify(flags) != MirrorRecordVersion.Rejected; }

        public static string ClassName(uint cls)
        {
            switch (cls)
            {
                case S5Conv.ClassCubePart: return "cube";
                case S5Conv.ClassEdge: return "edge";
                case S5Conv.ClassFatBlock: return "fat";
                case S5Conv.ClassSubpart: return "subpart";
                case S5Conv.ClassCharacterBody: return "character";
                case S5Conv.ClassTool: return "tool";
                case S5Conv.ClassFloatingObject: return "floating";
                default: return "class" + cls.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------- skeleton

 /// <summary>One row of the canonical skeleton table (<c>SeSkeleton.canon1.tsv</c>).</summary>
    internal sealed class CanonBone
    {
        public int Index, Parent;
        public string SeBone, SeParent, Human, Role, SnBone;
        public bool HasHuman { get { return Human != null && Human != "-"; } }
        public bool HasSnBone { get { return SnBone != null && SnBone != "?" && SnBone != "-"; } }
    }

    /// <summary>
 /// The canonical SE character skeleton canon1 (CONTRACT §5.4 "Character"; <c>src/DeepEngineers.Wire/Data/SeSkeleton.canon1.tsv</c>,
 /// embedded into this plugin as a resource): 78 rows, parents before children; the hash covers columns 1–3 only
 /// (<c>bone &lt;i&gt; &lt;seBone&gt; &lt;parent&gt;\n</c> per row, FNV-1a 64 = <see cref="S5Conv.SkeletonHash"/>).
 /// </summary>
    internal sealed class CanonSkeleton
    {
        public const string ResourceName = "DeepEngineers.SN.SeSkeleton.canon1.tsv";
        public const string Header = "index\tseBone\tparent\tseParent\thuman\trole\tsnBone";

        /// <summary>Unity's 15 required human bones (HumanBodyBones names; CONTRACT: Retarget needs all of them).</summary>
        public static readonly string[] RequiredHuman =
        {
            "Hips", "LeftUpperLeg", "RightUpperLeg", "LeftLowerLeg", "RightLowerLeg", "LeftFoot", "RightFoot", "Spine", "Head",
            "LeftUpperArm", "RightUpperArm", "LeftLowerArm", "RightLowerArm", "LeftHand", "RightHand",
        };

        public readonly List<CanonBone> Bones = new List<CanonBone>();
        public int[] Parents;
        private readonly Dictionary<string, int> _byName = new Dictionary<string, int>(StringComparer.Ordinal);

        public int Count { get { return Bones.Count; } }

        public int IndexOf(string seBone)
        {
            int i;
            return seBone != null && _byName.TryGetValue(seBone, out i) ? i : -1;
        }

        public int HumanIndex(string human)
        {
            foreach (CanonBone b in Bones) if (b.Human == human) return b.Index;
            return -1;
        }

        /// <summary>The hashed text (columns 1–3).</summary>
        public string SkeletonText()
        {
            var sb = new StringBuilder(Bones.Count * 32);
            foreach (CanonBone b in Bones)
                sb.Append("bone ").Append(b.Index.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(b.SeBone).Append(' ').Append(b.Parent.ToString(CultureInfo.InvariantCulture)).Append('\n');
            return sb.ToString();
        }

        public ulong Hash { get { return LayoutHash.Fnv1a64Ascii(SkeletonText()); } }

        /// <summary>Human column names that are missing from the 15 required ones (empty: every required bone is mapped).</summary>
        public List<string> MissingRequiredHuman()
        {
            var have = new HashSet<string>(StringComparer.Ordinal);
            foreach (CanonBone b in Bones) if (b.HasHuman) have.Add(b.Human);
            var missing = new List<string>();
            foreach (string r in RequiredHuman) if (!have.Contains(r)) missing.Add(r);
            return missing;
        }

        /// <summary>Required human bones whose row has no Subnautica bone (the generic retarget path G needs all 15).</summary>
        public List<string> MissingRequiredSnBones()
        {
            var missing = new List<string>();
            foreach (string r in RequiredHuman)
            {
                int i = HumanIndex(r);
                if (i < 0 || !Bones[i].HasSnBone) missing.Add(r);
            }
            return missing;
        }

        public static bool TryParse(string text, out CanonSkeleton sk, out string error)
        {
            sk = null;
            error = null;
            if (text == null) { error = "no text"; return false; }
            var s = new CanonSkeleton();
            bool header = false;
            int lineNo = 0;
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                lineNo++;
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;
                if (!header)
                {
                    if (line != Header) { error = "line " + lineNo + ": header '" + line + "' != '" + Header.Replace("\t", "\\t") + "'"; return false; }
                    header = true;
                    continue;
                }
                string[] c = line.Split('\t');
                if (c.Length != 7) { error = "line " + lineNo + ": " + c.Length + " columns (7 expected)"; return false; }
                int idx, parent;
                if (!int.TryParse(c[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out idx) || idx != s.Bones.Count)
                { error = "line " + lineNo + ": index '" + c[0] + "' (expected " + s.Bones.Count + ")"; return false; }
                if (!int.TryParse(c[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out parent) || parent < -1 || parent >= idx)
                { error = "line " + lineNo + ": parent '" + c[2] + "' must precede its child"; return false; }
                if (parent >= 0 && s.Bones[parent].SeBone != c[3]) { error = "line " + lineNo + ": seParent '" + c[3] + "' != row " + parent; return false; }
                if (s._byName.ContainsKey(c[1])) { error = "line " + lineNo + ": duplicate bone '" + c[1] + "'"; return false; }
                var b = new CanonBone { Index = idx, SeBone = c[1], Parent = parent, SeParent = c[3], Human = c[4], Role = c[5], SnBone = c[6] };
                s._byName[b.SeBone] = idx;
                s.Bones.Add(b);
            }
            if (!header) { error = "no header line"; return false; }
            s.Parents = new int[s.Bones.Count];
            for (int i = 0; i < s.Bones.Count; i++) s.Parents[i] = s.Bones[i].Parent;
            sk = s;
            return true;
        }

        private static CanonSkeleton _embedded;
        private static string _embeddedError;

        /// <summary>The table embedded into this assembly (parsed once; null with <paramref name="error"/> when missing or invalid).</summary>
        public static CanonSkeleton Embedded(out string error)
        {
            if (_embedded != null || _embeddedError != null) { error = _embeddedError; return _embedded; }
            try
            {
                Assembly a = typeof(CanonSkeleton).Assembly;
                using (Stream st = a.GetManifestResourceStream(ResourceName))
                {
                    if (st == null) { _embeddedError = "resource " + ResourceName + " not found"; error = _embeddedError; return null; }
                    using (var r = new StreamReader(st, Encoding.UTF8))
                    {
                        CanonSkeleton s;
                        string e;
                        if (!TryParse(r.ReadToEnd(), out s, out e)) { _embeddedError = e; error = e; return null; }
                        if (s.Count != S5Conv.CanonicalBones) { _embeddedError = s.Count + " bones (78 expected)"; error = _embeddedError; return null; }
                        if (s.Hash != S5Conv.SkeletonHash) { _embeddedError = "skeleton hash " + DeLog.Hex(s.Hash) + " != " + DeLog.Hex(S5Conv.SkeletonHash); error = _embeddedError; return null; }
                        _embedded = s;
                    }
                }
            }
            catch (Exception e)
            {
                _embeddedError = e.GetType().Name + ": " + e.Message;
            }
            error = _embeddedError;
            return _embedded;
        }
    }

    // ---------------------------------------------------------------------------------------------------------- switches

    internal enum FpBody { Diver = 0, Se = 1 }

    /// <summary>
 /// The first-person body switch (CONTRACT §5.4 "Character"): <c>de.s5 fpbody=se|diver</c>, env
 /// <c>DEEPENGINEERS_SN_FPBODY</c>; S5a's
 /// diver arms behind <c>diver</c>).
 /// </summary>
    internal static class S5Switches
    {
        public const string FpBodyVar = "DEEPENGINEERS_SN_FPBODY";

        public static bool TryParseFpBody(string s, out FpBody v)
        {
            v = FpBody.Diver;
            if (s == null) return false;
            switch (s.Trim().ToLowerInvariant())
            {
                case "se": v = FpBody.Se; return true;
                case "diver": v = FpBody.Diver; return true;
                default: return false;
            }
        }

        /// FromEnvironmentValue"/>).</summary>
        public static FpBody FpBodyFromEnvironment(out string note)
        {
            string v = null;
            try { v = Environment.GetEnvironmentVariable(FpBodyVar); } catch (Exception) { }
            return S7pFirstPerson.FromEnvironmentValue(v, out note);
        }

        public static string Name(FpBody b) { return b == FpBody.Se ? "se" : "diver"; }
    }

    /// <summary>
 /// The Subnautica halves of S5.M4 and S5.M5 (ROADMAP S5 "Seeded failures"), applied after S2's recipe selection
 /// (<see cref="PerfRecipeRules.Select"/>): S5.M4 = Full instead of Idle while C5 says Space (the T-P1 Space
 /// criterion must turn red), S5.M5 = the cameras left on in Track (the T-P1 Track criterion must turn red).
 /// </summary>
    internal static class S5PerfMutants
    {
        public static void Apply(ref RecipeDecision d, ref RecipeInputs i, bool fullInSpace, bool camerasOnInTrack)
        {
            if (fullInSpace && i.C5Valid && i.PeerAlive && i.ViewState == Wire.ViewState.Space && d.Recipe == Wire.PerfMode.Idle)
            {
                d = new RecipeDecision
                {
                    Recipe = Wire.PerfMode.Full, VSyncCount = 1, TargetFrameRate = -1, CamerasOn = true, BelowNormal = false, IdleCause = false,
                    Why = d.Why + "; MUTANT S5.M4 Full instead of Idle in Space",
                };
            }
            if (camerasOnInTrack && d.Recipe == Wire.PerfMode.Track && !d.CamerasOn)
            {
                d.CamerasOn = true;
                d.Why = d.Why + "; MUTANT S5.M5 cameras left on in Track";
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------- chunk builds

 /// <summary>A (grid, cell) pair: the unit of a chunk build (allocation-free dictionary key).</summary>
    internal struct GridCell : IEquatable<GridCell>
    {
        public readonly ulong Grid;
        public readonly CellKey Cell;
        public GridCell(ulong grid, CellKey cell) { Grid = grid; Cell = cell; }
        public bool Equals(GridCell o) { return Grid == o.Grid && Cell.Equals(o.Cell); }
        public override bool Equals(object obj) { return obj is GridCell && Equals((GridCell)obj); }
        public override int GetHashCode() { return Grid.GetHashCode() * 486187739 ^ Cell.GetHashCode(); }
        public override string ToString() { return Grid.ToString("x", CultureInfo.InvariantCulture) + "/" + Cell; }
    }

    /// <summary>
 /// A grid's dirty cells with the frame each was last dirtied in (the "dirty-newest" order of CONTRACT §5.4
 /// "Budgets"); enumerable like the HashSet it replaces.
 /// </summary>
    internal sealed class DirtyCellSet : IEnumerable<CellKey>
    {
        private readonly Dictionary<CellKey, long> _frame = new Dictionary<CellKey, long>();
        /// <summary>The consumer's frame stamped on cells dirtied from now on (set by the consumer every frame).</summary>
        public static long CurrentFrame;

        public int Count { get { return _frame.Count; } }
        public bool Add(CellKey c) { bool had = _frame.ContainsKey(c); _frame[c] = CurrentFrame; return !had; }
        public bool Remove(CellKey c) { return _frame.Remove(c); }
        public bool Contains(CellKey c) { return _frame.ContainsKey(c); }
        public void Clear() { _frame.Clear(); }
        public long FrameOf(CellKey c) { long f; return _frame.TryGetValue(c, out f) ? f : long.MinValue; }
        public Dictionary<CellKey, long>.KeyCollection.Enumerator GetEnumerator() { return _frame.Keys.GetEnumerator(); }
        IEnumerator<CellKey> IEnumerable<CellKey>.GetEnumerator() { return _frame.Keys.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() { return _frame.Keys.GetEnumerator(); }
    }

    /// <summary>
 /// CONTRACT §5.4 "Chunk key and consumer rules": dirty cells are built only from a state where the frame's C4 drain
 /// reached the head read at its start (no half-applied edit is drawn), or after 10 frames regardless.
 /// </summary>
    internal sealed class DrainGate
    {
        public const int MaxDeferredFrames = 10;
        private int _deferred;
        public long DeferredFrames, ForcedFrames;

        public bool Allow(bool reachedHead)
        {
            if (reachedHead) { _deferred = 0; return true; }
            _deferred++;
            DeferredFrames++;
            if (_deferred < MaxDeferredFrames) return false;
            _deferred = 0;
            ForcedFrames++;
            return true;
        }
    }

    /// <summary>A dirty cell waiting for its build (ordering only).</summary>
    internal struct CellCandidate
    {
        public GridCell Key;
        public long DirtyFrame;
        public double Distance;
    }

    /// <summary>
 /// The build queue rules (CONTRACT §5.4 "Budgets": worker builds, one in flight per chunk key, ≤ 256 queued,
 /// dirty-newest then nearest). Unity-free bookkeeping; the consumer posts what <see cref="Pick"/> returns.
 /// </summary>
    internal sealed class CellBuildQueue
    {
        public const int MaxInFlight = 256;
        private readonly HashSet<GridCell> _inFlight = new HashSet<GridCell>();
        private readonly List<CellCandidate> _candidates = new List<CellCandidate>(256);
        private static readonly IComparer<CellCandidate> Order = new CandidateOrder();

        private sealed class CandidateOrder : IComparer<CellCandidate>
        {
            public int Compare(CellCandidate a, CellCandidate b) { return CellBuildQueue.Compare(a, b); }
        }
        public long Posted, Completed, SkippedInFlight, Capped;
        /// <summary>S10 hook (part 3): the distance-first order (Wire S10BuildOrder; set by the consumer every frame); false =
 /// the S5 rule (dirty-newest then nearest), which the S5 tests pin.</summary>
        public bool DistanceOrder;
        public double NearM = S10BuildOrder.DefaultNearM;
        private readonly IComparer<CellCandidate> _s10Order;

        public CellBuildQueue() { _s10Order = new S10Order(this); }

        private sealed class S10Order : IComparer<CellCandidate>
        {
            private readonly CellBuildQueue _q;
            public S10Order(CellBuildQueue q) { _q = q; }
            public int Compare(CellCandidate a, CellCandidate b)
            {
                int c = S10BuildOrder.Compare(a.DirtyFrame, a.Distance, b.DirtyFrame, b.Distance, _q.NearM);
                return c != 0 ? c : CellBuildQueue.Compare(a, b);
            }
        }

        public int InFlight { get { return _inFlight.Count; } }
        public bool IsInFlight(GridCell k) { return _inFlight.Contains(k); }

        public static int Compare(CellCandidate a, CellCandidate b)
        {
            int c = b.DirtyFrame.CompareTo(a.DirtyFrame); // newest dirty first
            if (c != 0) return c;
            c = a.Distance.CompareTo(b.Distance);         // then nearest
            if (c != 0) return c;
            c = a.Key.Grid.CompareTo(b.Key.Grid);
            if (c != 0) return c;
            c = a.Key.Cell.X.CompareTo(b.Key.Cell.X);
            if (c != 0) return c;
            c = a.Key.Cell.Y.CompareTo(b.Key.Cell.Y);
            return c != 0 ? c : a.Key.Cell.Z.CompareTo(b.Key.Cell.Z);
        }

        public void BeginFrame() { _candidates.Clear(); }

        /// <summary>A dirty cell whose models are ready; a cell with a build in flight waits (rebuilt once more after).</summary>
        public void Offer(CellCandidate c)
        {
            if (_inFlight.Contains(c.Key)) { SkippedInFlight++; return; }
            _candidates.Add(c);
        }

        /// <summary>The cells to post now, in order, at most up to <see cref="MaxInFlight"/> in flight.</summary>
        public List<CellCandidate> Pick()
        {
            if (_candidates.Count > 1) _candidates.Sort(DistanceOrder ? _s10Order : Order); // shared comparers: no allocation per frame
            int room = MaxInFlight - _inFlight.Count;
            if (room < 0) room = 0;
            if (_candidates.Count > room) { Capped += _candidates.Count - room; _candidates.RemoveRange(room, _candidates.Count - room); }
            return _candidates;
        }

        public void MarkPosted(GridCell k) { if (_inFlight.Add(k)) Posted++; }
        public void MarkDone(GridCell k) { if (_inFlight.Remove(k)) Completed++; }
        public void Clear() { _inFlight.Clear(); _candidates.Clear(); }
    }

    // ---------------------------------------------------------------------------------------------------------- frame statistics

 /// <summary>Per-second frame-time statistics (s5.scale / s5.perf rows): p50, p99, max over the frames of the window.</summary>
    internal sealed class FrameStats
    {
        private readonly double[] _ms = new double[1024];
        private readonly double[] _scratch = new double[1024];
        private int _n;

        public void Add(double ms)
        {
            if (_n < _ms.Length) _ms[_n++] = ms;
        }

        public int Count { get { return _n; } }

        /// <summary>The window's percentiles (nearest rank); the window restarts.</summary>
        public void Take(out double p50, out double p99, out double max, out int frames)
        {
            int over;
            Take(out p50, out p99, out max, out frames, out over);
        }

        /// <summary>Review F8: <paramref name="over"/> = frames above 16.7 ms in the window (the faithful p99 criterion is a share of frames).</summary>
        public void Take(out double p50, out double p99, out double max, out int frames, out int over)
        {
            over = 0;
            for (int i = 0; i < _n; i++) if (_ms[i] > 16.7) over++;
            frames = _n;
            p50 = p99 = max = double.NaN;
            if (_n == 0) return;
            Array.Copy(_ms, _scratch, _n);
            Array.Sort(_scratch, 0, _n);
            p50 = _scratch[Rank(0.50, _n)];
            p99 = _scratch[Rank(0.99, _n)];
            max = _scratch[_n - 1];
            _n = 0;
        }

        public static int Rank(double p, int n)
        {
            int r = (int)Math.Ceiling(p * n) - 1;
            return r < 0 ? 0 : (r >= n ? n - 1 : r);
        }
    }

    // ---------------------------------------------------------------------------------------------------------- character source

    internal enum CharSource { None = 0, Bind = 1, SeBones = 2, Retarget = 3, Blend = 4 /* S13 point 6: the swim legs under SE's upper body */ }

    /// <summary>What the character's animation source selection reads each camera sample (CONTRACT §5.4 "Character").</summary>
    internal struct CharSourceInputs
    {
        /// <summary>C1 bones valid for the current character (boneCount 78, bonesVersion = low 32 bits of its body modelKey, same id).</summary>
        public bool BonesValid;
        public bool SwimValid;
        public byte SwimFlags, SwimMode;
        public bool Seated, Dead;
        /// <summary>A retarget path (H or G) is set up and maps all 15 required bones, and is not switched off by its budget.</summary>
        public bool RetargetReady;
        /// </summary>
        public bool ToolHeld;
        /// <summary>S13 hook: the C1 locomotion extension says the character is on a ladder (SE's ladder pose, never the swim).</summary>
        public bool Ladder;
    }

    internal static class CharSourceRules
    {
        public const double BlendSeconds = 0.25;

        /// <summary>Swimming by the contract: extension version 1, inWater, mode Swim or Surface, not grounded, not seated or dead.</summary>
        public static bool Swimming(ref CharSourceInputs i)
        {
            // S13 hook (point 2): ground contact cancels the swim only at the surface; submerged the swim holds (S13Source)
            return S13Source.Swimming(i.SwimValid, i.SwimFlags, i.SwimMode, i.Seated || i.Ladder, i.Dead, DevMutants.IsOn(S13SnMutants.GroundedCancels));
        }

        /// <summary>
 /// Retarget (the swim strokes) while swimming, except with a gun or tool in hand: the weapon is mirrored where SE's

 /// hands show in first person"); otherwise SE's bones; Bind when they are not valid.
 /// </summary>
        public static CharSource Select(ref CharSourceInputs i)
        {
            // S13 hook (point 6): swimming with a held tool, gun or block = Blend (the legs swim, SE's arms hold the item)
            return S13Source.Select(Swimming(ref i), i.RetargetReady, i.ToolHeld, i.BonesValid, DevMutants.IsOn(S13SnMutants.HeldSeBones));
        }

        /// <summary>Blend weight of the new source after <paramref name="seconds"/> (0.25 s per-bone blend; HYPOTHESIS for the look).</summary>
        public static double BlendWeight(double seconds)
        {
            if (!(seconds > 0)) return 0;
            return seconds >= BlendSeconds ? 1.0 : seconds / BlendSeconds;
        }

        /// <summary>
 /// Whether a character part is drawn: HiddenInFirstPerson parts are not drawn while C1 cameraMode is FirstPerson
 /// (S5.N7b); with fpbody=diver no SE body part is drawn in first person (S5a's diver arms are, the two never together).
 /// </summary>
        public static bool PartDrawn(uint partFlags, uint cameraMode, FpBody fp)
        {
            bool first = cameraMode == CameraMode.FirstPerson;
            if (!first) return true;
            if (fp == FpBody.Diver) return false;
            return (partFlags & S5Conv.HiddenInFirstPerson) == 0;
        }

        public static string Name(CharSource s)
        {
            switch (s)
            {
                case CharSource.Bind: return "Bind";
                case CharSource.SeBones: return "SeBones";
                case CharSource.Retarget: return "Retarget";
                case CharSource.Blend: return "Blend"; // S13 hook (point 6)
                default: return "None";
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------- rows

 /// <summary>
 /// The Subnautica s5.* JSONL rows (ROADMAP S5 "Builder contract": s5.model, s5.grid, s5.placed, s5.edit, s5.sub,
 /// s5.coh, s5.scale, s5.char, s5.rig, s5.perf): {"t", "tickMs", "frame", "level", "event", "ev", "data": {…}}, the
 /// S4 row shape. Written to the JSONL only (DeLog.AppendJsonlBatch).
 /// </summary>
    internal static class S5Rows
    {
        private static readonly StringBuilder Sb = new StringBuilder(2048);
        public static long Written;

        public static JsonOut Begin(string ev)
        {
            var j = new JsonOut();
            j.Str("t", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Num("tickMs", Win32.TickCount64())
             .Num("frame", DeLog.Frame).Str("level", "info").Str("event", ev).Str("ev", ev);
            j.Obj("data");
            return j;
        }

        /// <summary>Closes the data object and writes the row (main thread).</summary>
        public static void End(JsonOut j)
        {
            j.EndObj();
            string s = j.ToString();
            Sb.Length = 0;
            Sb.Append(s).Append('\n');
            DeLog.AppendJsonlBatch(Sb);
            Written++;
        }

        public static JsonOut Rows12(JsonOut j, string name, float[] a, int off)
        {
            j.Arr(name);
            for (int i = 0; i < 12; i++) j.Num(null, (double)a[off + i]);
            return j.EndArr();
        }

        public static JsonOut Rows12(JsonOut j, string name, double[] a)
        {
            j.Arr(name);
            for (int i = 0; i < 12; i++) j.Num(null, a[i]);
            return j.EndArr();
        }

        public static string Hex(ulong v) { return "0x" + v.ToString("x16", CultureInfo.InvariantCulture); }
    }

    /// <summary>A deterministic per-part sample for s5.placed (S5.N1.C2): the parts with the smallest splitmix64(partKey).</summary>
    internal static class PartSample
    {
        public static ulong Mix(ulong z)
        {
            z += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>Up to <paramref name="n"/> keys with the smallest <see cref="Mix"/>, ascending.</summary>
        public static List<ulong> Pick(IEnumerable<ulong> keys, int n)
        {
            var all = new List<KeyValuePair<ulong, ulong>>();
            foreach (ulong k in keys) all.Add(new KeyValuePair<ulong, ulong>(Mix(k), k));
            all.Sort((a, b) => a.Key.CompareTo(b.Key));
            var r = new List<ulong>(Math.Min(n, all.Count));
            for (int i = 0; i < all.Count && i < n; i++) r.Add(all[i].Value);
            return r;
        }
    }
}
