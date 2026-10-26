using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using VRage;
using VRage.Game.Models;
using VRage.Utils;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// Reads a grid's armor parts the way SE's own exporter does
 /// every <c>MyCubePart</c> key of every render cell of
 /// <c>grid.RenderData.Cells</c> (<c>ConcurrentDictionary&lt;Vector3I, MyCubeGridRenderCell&gt;</c>) →
 /// <c>MyCubeGridRenderCell.CubeParts</c> (<c>ConcurrentDictionary&lt;MyCubePart, ConcurrentDictionary&lt;uint, bool&gt;&gt;</c>;
 /// enumerating a ConcurrentDictionary is safe while SE's parallel dirty-region update changes it, and the producer
 /// debounces the reads, <see cref="PartSetTracker"/>). Per part: <c>MyCubePart.Model</c> (<c>MyModel</c>:
 /// <c>AssetName</c>, <c>ScaleFactor</c>), <c>MyCubePart.SkinSubtypeId</c> (<c>MyStringHash</c>),
 /// <c>MyCubePart.InstanceData</c> (<c>MyCubeInstanceData</c>: <c>LocalMatrix</c> (grid-local; translation = block
 /// position × grid size, VERIFIED <c>GetCubeParts</c>), <c>ColorMaskHSV</c> (Vector4, xyz used as SE's exporter does),
 /// <c>PackedOrthoMatrix</c>, <c>GetPackedBone(int)</c> → <c>Vector4UByte</c> (the W bytes 5, 6, 7 hold the armor
 /// pattern offset, <see cref="PartUv"/>), <c>GetTextureOffset(int)</c> (logged only: always 0, see <see cref="PartUv"/>)).
 /// Update thread. Armor edges (<c>MyCubeGridRenderCell</c>'s private edge set) and fat blocks are not read in S3.
 /// </summary>
    internal static class ArmorParts
    {
        /// <summary>Fills <paramref name="into"/> with the grid's current parts (keyed by part key). Returns the part count.</summary>
        public static int Read(MyCubeGrid grid, Dictionary<ulong, PartSnapshot> into, out int fatBlocks)
        {
            into.Clear();
            fatBlocks = 0;
            MyCubeGridRenderData rd = grid.RenderData;
            if (rd == null) return 0;
            foreach (KeyValuePair<Vector3I, MyCubeGridRenderCell> cell in rd.Cells)
            {
                MyCubeGridRenderCell c = cell.Value;
                if (c == null) continue;
                foreach (var kv in c.CubeParts)
                {
                    MyCubePart part = kv.Key;
                    if (part == null || part.Model == null) continue;
                    PartSnapshot p = Snapshot(part);
                    into[p.PartKey] = p;
                }
            }
            foreach (MySlimBlock b in grid.GetBlocks())
                if (b != null && b.FatBlock != null) fatBlocks++;
            return into.Count;
        }

        private static PartSnapshot Snapshot(MyCubePart part)
        {
            MyModel model = part.Model;
            string asset = model.AssetName;
            float scale = model.ScaleFactor;
            ulong modelKey = S3Keys.ModelKey(asset, 0, scale);
            Matrix m = part.InstanceData.LocalMatrix;
            Vector4 packed = part.InstanceData.PackedOrthoMatrix;
            Vector4 hsv = part.InstanceData.ColorMaskHSV;
            Vector4UByte b5 = part.InstanceData.GetPackedBone(5);
            Vector4UByte b6 = part.InstanceData.GetPackedBone(6);
            Vector4UByte b7 = part.InstanceData.GetPackedBone(7);
            float u, v;
            PartUv.Offset(b5.W, b6.W, b7.W, out u, out v);
            MyStringHash skin = part.SkinSubtypeId;
            return new PartSnapshot
            {
                PartKey = PartDiff.PartKey(modelKey, packed.X, packed.Y, packed.Z, packed.W),
                ModelKey = modelKey,
                AssetName = asset,
                ModelScale = scale,
                Local = new[] { m.M11, m.M12, m.M13, m.M21, m.M22, m.M23, m.M31, m.M32, m.M33, m.M41, m.M42, m.M43 },
                H = hsv.X, S = hsv.Y, V = hsv.Z,
                UvU = u, UvV = v,
                SkinId = skin == MyStringHash.NullOrEmpty ? 0u : unchecked((uint)skin.GetHashCode()),
                LegacyU = part.InstanceData.GetTextureOffset(0),
                LegacyV = part.InstanceData.GetTextureOffset(1),
            };
        }
    }
}
