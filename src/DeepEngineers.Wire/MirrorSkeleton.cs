using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeepEngineers.Wire
{
    /// <summary>The <c>role</c> column of the canonical skeleton (how a bone behaves in a retarget).</summary>
    public enum MirrorBoneRole
    {
        /// <summary>No retarget: the body transform drives it.</summary>
        Root = 0,
        /// <summary>Retargeted (a Unity human bone).</summary>
        Human = 1,
        /// <summary>Forearm twist: keeps its rest local transform in a retarget.</summary>
        Twist = 2,
        /// <summary>Unmapped deforming bone: keeps its rest local transform in a retarget.</summary>
        Follow = 3,
        /// <summary>Attachment point (camera, lights, weapon pins, jetpack): rest local transform in a retarget.</summary>
        Dummy = 4,
    }

    /// <summary>
 /// The canonical SE character skeleton <c>canon1</c> (CONTRACT §5.4 "Character"; data file
 /// <c>src/DeepEngineers.Wire/Data/SeSkeleton.canon1.tsv</c>, compiled into <c>MirrorSkeleton.Canon1.cs</c> by
 /// <c>python -I tools/worldmap/wire_ref.py s5skeleton-cs src/DeepEngineers.Wire/MirrorSkeleton.Canon1.cs</c>, D15 style: C# arrays, not an embedded resource). Every bone
 /// index on the wire is a canonical index: vertex <c>boneIndex</c>, the SkinnedBind order, the C1 bone order. Parents
 /// precede children. The skeleton hash covers columns 1-3 only (one <c>bone &lt;i&gt; &lt;seBone&gt; &lt;parent&gt;\n</c>
 /// line per row, FNV-1a 64 <c>0x995765282e7d4f1d</c>); the <c>human</c>, <c>role</c> and <c>snBone</c> columns are
 /// Subnautica retarget hints that may change without a re-pin (regenerate the C# table; MirrorConventionsTests compares
 /// it with the data file).
 /// </summary>
    public static partial class MirrorSkeleton
    {
        /// <summary>The skeleton's name in the conventions text.</summary>
        public const string Name = "canon1";
        /// <summary>78 bones.</summary>
        public const int BoneCount = MirrorLimits.CanonicalBones;
        /// <summary>FNV-1a 64 of <see cref="SkeletonText"/>.</summary>
        public const ulong PinnedHash = 0x995765282e7d4f1dUL;
        /// <summary>The data file's column header line (tab-separated).</summary>
        public const string TsvHeader = "index\tseBone\tparent\tseParent\thuman\trole\tsnBone";

        /// <summary>Rows used by S5.N7a and the first-person anchor.</summary>
        public const int RootNode = 0, MainRoot = 1, Pelvis = 2, Head = 13, HeadDummy = 14, LeftPalm = 29, RightPalm = 52;

        /// <summary>The 15 Unity human bones a humanoid avatar requires (HumanTrait.RequiredBone; CONTRACT §5.4).</summary>
        public static readonly string[] RequiredHumanBones =
        {
            "Hips", "LeftUpperLeg", "RightUpperLeg", "LeftLowerLeg", "RightLowerLeg", "LeftFoot", "RightFoot", "Spine", "Head",
            "LeftUpperArm", "RightUpperArm", "LeftLowerArm", "RightLowerArm", "LeftHand", "RightHand",
        };

        private static readonly Dictionary<string, int> ByName;
        private static readonly Dictionary<string, int> ByHuman;
        private static readonly MirrorBoneRole[] RoleValues;
        private static readonly ulong ComputedHash;

        static MirrorSkeleton()
        {
            // A static constructor runs after every static field initializer of every part (the generated arrays).
            ByName = new Dictionary<string, int>(StringComparer.Ordinal);
            ByHuman = new Dictionary<string, int>(StringComparer.Ordinal);
            RoleValues = new MirrorBoneRole[BoneCount];
            for (int i = 0; i < BoneCount; i++)
            {
                ByName[Names[i]] = i;
                if (Humans[i] != "-") ByHuman[Humans[i]] = i;
                RoleValues[i] = ParseRole(Roles[i]);
            }
            ComputedHash = LayoutHash.Fnv1a64(Encoding.UTF8.GetBytes(SkeletonText()));
        }

        private static MirrorBoneRole ParseRole(string role)
        {
            switch (role)
            {
                case "root": return MirrorBoneRole.Root;
                case "human": return MirrorBoneRole.Human;
                case "twist": return MirrorBoneRole.Twist;
                case "follow": return MirrorBoneRole.Follow;
                case "dummy": return MirrorBoneRole.Dummy;
                default: throw new InvalidOperationException("unknown skeleton role " + role);
            }
        }

        /// <summary>The SE bone name (<c>MyModelBone.Name</c>) of canonical row i.</summary>
        public static string BoneName(int index) { return Names[index]; }

        /// <summary>The canonical parent of row i (−1 for the root).</summary>
        public static int Parent(int index) { return Parents[index]; }

        /// <summary>The SE name of row i's parent (null for the root).</summary>
        public static string ParentName(int index) { int p = Parents[index]; return p < 0 ? null : Names[p]; }

        /// <summary>The Unity <c>HumanBodyBones</c> enum name of row i, null when it has none.</summary>
        public static string Human(int index) { string h = Humans[index]; return h == "-" ? null : h; }

        /// <summary>The retarget role of row i.</summary>
        public static MirrorBoneRole Role(int index) { return RoleValues[index]; }

        /// <summary>The Subnautica rig bone of row i for the generic retarget path; null while it is <c>?</c> (HYPOTHESIS) or <c>-</c>.</summary>
        public static string SnBone(int index) { string s = SnBones[index]; return s == "?" || s == "-" ? null : s; }

        /// <summary>The canonical index of an SE bone name (ordinal), −1 when it is not canonical.</summary>
        public static int IndexOf(string seBone)
        {
            int i;
            return seBone != null && ByName.TryGetValue(seBone, out i) ? i : -1;
        }

        /// <summary>The canonical index mapped to a Unity human bone name, −1 when none is.</summary>
        public static int IndexOfHuman(string humanBone)
        {
            int i;
            return humanBone != null && ByHuman.TryGetValue(humanBone, out i) ? i : -1;
        }

        /// <summary>True when every one of the 15 required human bones is mapped by the <c>human</c> column.</summary>
        public static bool MapsAllRequiredHumanBones()
        {
            foreach (string h in RequiredHumanBones) if (IndexOfHuman(h) < 0) return false;
            return true;
        }

        /// <summary>The hashed text: one <c>bone &lt;index&gt; &lt;seBone&gt; &lt;parent&gt;\n</c> line per row (parent −1 for the root).</summary>
        public static string SkeletonText()
        {
            var sb = new StringBuilder(2200);
            for (int i = 0; i < BoneCount; i++)
                sb.Append("bone ").Append(i.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Names[i]).Append(' ')
                  .Append(Parents[i].ToString(CultureInfo.InvariantCulture)).Append('\n');
            return sb.ToString();
        }

        /// <summary>FNV-1a 64 of <see cref="SkeletonText"/> as compiled (equals <see cref="PinnedHash"/> when nothing drifted).</summary>
        public static ulong Hash { get { return ComputedHash; } }

        /// <summary>Row i exactly as the data file holds it (7 tab-separated columns), for the drift test.</summary>
        public static string TsvRow(int index)
        {
            int p = Parents[index];
            return index.ToString(CultureInfo.InvariantCulture) + "\t" + Names[index] + "\t" + p.ToString(CultureInfo.InvariantCulture) + "\t"
                + (p < 0 ? "-" : Names[p]) + "\t" + Humans[index] + "\t" + Roles[index] + "\t" + SnBones[index];
        }

        /// <summary>The canonical parent of every row (a copy).</summary>
        public static int[] ParentTable()
        {
            var a = new int[BoneCount];
            Array.Copy(Parents, a, BoneCount);
            return a;
        }
    }
}
