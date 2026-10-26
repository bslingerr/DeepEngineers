using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;
using HarmonyLib;
using UnityEngine;
using WorldStreaming;

namespace DeepEngineers.SN
{
    /// <summary>
 /// docs/S8A-PLANET-V2.md section
 /// 13; CONTRACT §5.4 format convert.orelook, ext s8a terrain 6). Subnautica's terrain palette (<c>VoxelandBlockType[]</c>, one
 /// Material per type, indexed by the octree's type byte) reaches the streaming mesher through
 /// <c>WorldStreamer.Start(VoxelandBlockType[] blockTypes, WorldStreamer.Settings settings)</c>
 /// A <b>prefix</b> replaces the array with a copy whose null slots of the ore rows
 /// (<see cref="S8aMaterialMap.SnTypes"/> 7..15: 95, 96, 107, 108, 117, 118, 119, 121, 122) hold this mod's ore block types:
 /// <c>VoxelandBlockType.FilledMaterial(Material, string)</c> (VoxelandBlockType.cs) with the template rock's layer (GreyRock) and
 /// <c>new Material(template.material)</c> (the terrain shader, keywords, caustics, fog and day cycle as the rock's) whose albedo
 /// textures are replaced by our generated ore texture (dark rock, veins and crystals in the ore's colour; never a game asset),
 /// then <c>RuntimeInit(int)</c> (its opaque and alpha-test copies). A slot the game's palette already fills is left alone.
 /// The texture properties are read from the cloned material at run time (<c>Material.GetTexturePropertyNames</c>) and logged
 /// (row <c>s8a.palette.ore</c>): a name that reads as a normal, mask or noise map is kept. <c>de.s8a orelook {as: type}</c> copies
 /// another palette type's material into the ore materials in place (the paint test of section 13's fallback: every ore voxel shows
 /// that type), <c>{as: "ours"}</c> restores ours. Off with DEEPENGINEERS_S8A_ORE_LOOK=off (the slots then take the template rock,
 /// so ore voxels still draw).
 /// </summary>
    internal static class S8aOrePalette
    {
        public const string EnvSwitch = "DEEPENGINEERS_S8A_ORE_LOOK";
        public const int TextureSize = 256;
        /// <summary>The ores' colours (sRGB), in <see cref="S8aMaterialMap.Subtypes"/> ore order: Iron rust-red, Nickel green-grey, Silicon white
 /// quartz, Cobalt blue, Magnesium pale violet, Silver bright grey, Gold yellow, Platinum white-blue, Uranium green.</summary>
        public static readonly Color32[] OreColours =
        {
            new Color32(186, 70, 34, 255), new Color32(118, 156, 104, 255), new Color32(232, 232, 226, 255), new Color32(46, 92, 214, 255),
            new Color32(186, 146, 228, 255), new Color32(208, 214, 220, 255), new Color32(236, 182, 36, 255), new Color32(204, 228, 248, 255),
            new Color32(96, 236, 70, 255),
        };
        private static readonly string[] KeepWords = { "bump", "normal", "nrm", "spec", "gloss", "emission", "illum", "noise", "mask", "caustic", "detail", "height", "parallax", "occlusion", "_ao" };

        public static bool Patched { get; private set; }
        public static bool Applied { get; private set; }
        public static string TemplateName { get; private set; }
        public static string Look { get; private set; }
        public static int Filled, Taken, TexturesReplaced;
        private static readonly List<Dictionary<string, object>> SlotRows = new List<Dictionary<string, object>>();
        private static readonly Dictionary<int, Material> Own = new Dictionary<int, Material>();
        private static readonly Dictionary<int, Material> Live = new Dictionary<int, Material>();
        private static VoxelandBlockType[] _types;
        private static string _props;

        /// <summary>The ore palette slots in ore order (the conventions' table: the converter writes these types).</summary>
        public static int[] Slots()
        {
            var s = new int[S8aMaterialMap.Subtypes.Length - S8aMaterialMap.SurfaceMaterials];
            for (int i = 0; i < s.Length; i++) s[i] = S8aMaterialMap.SnTypes[S8aMaterialMap.SurfaceMaterials + i];
            return s;
        }

        public static void Install(Harmony h)
        {
            try
            {
                var m = AccessTools.Method(typeof(WorldStreamer), "Start", new[] { typeof(VoxelandBlockType[]), typeof(WorldStreamer.Settings) });
                if (m == null || m.IsStatic || m.ReturnType != typeof(void))
                {
                    DeLog.Warn("DE.S8a MISSING WorldStreamer.Start(VoxelandBlockType[], WorldStreamer.Settings) (ore voxels draw nothing: their palette slots stay empty)");
                    return;
                }
                h.Patch(m, prefix: new HarmonyMethod(typeof(S8aOrePalette).GetMethod(nameof(Prefix_Start), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
                Patched = true;
            }
            catch (Exception e) { DeLog.Warn("DE.S8a MISSING ore palette patch: " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary><c>WorldStreamer.Start</c> prefix (__0 = blockTypes, by ref): the extended copy. Never throws (a failure keeps the game's array).</summary>
        private static void Prefix_Start(ref VoxelandBlockType[] __0)
        {
            try { __0 = Extend(__0); }
            catch (Exception e) { DeLog.Warn("DE.S8a ore palette not applied: " + e.GetType().Name + ": " + e.Message); }
        }

        private static VoxelandBlockType Template(VoxelandBlockType[] types)
        {
            for (int i = 0; i < types.Length; i++)
                if (types[i] != null && types[i].filled && types[i].material != null && string.Equals(types[i].name, "GreyRock", StringComparison.OrdinalIgnoreCase)) return types[i];
            if (types.Length > 16 && types[16] != null && types[16].material != null) return types[16];
            for (int i = 1; i < types.Length; i++)
                if (types[i] != null && types[i].filled && types[i].material != null) return types[i];
            return null;
        }

        public static VoxelandBlockType[] Extend(VoxelandBlockType[] types)
        {
            if (types == null) return null;
            int[] slots = Slots();
            int max = 0;
            foreach (int s in slots) max = Math.Max(max, s);
            var t = new VoxelandBlockType[Math.Max(types.Length, max + 1)];
            Array.Copy(types, t, types.Length);
            VoxelandBlockType tpl = Template(types);
            if (tpl == null) { DeLog.Warn("DE.S8a ore palette: no template rock in the palette (ore voxels draw nothing)"); return types; }
            TemplateName = tpl.name;
            bool on = !string.Equals(Environment.GetEnvironmentVariable(EnvSwitch), "off", StringComparison.OrdinalIgnoreCase);
            var names = new List<string>();
            try { foreach (string n in tpl.material.GetTexturePropertyNames()) names.Add(n); } catch (Exception) { }
            var described = new List<string>();
            foreach (string n in names)
            {
                Texture tx = null;
                try { tx = tpl.material.GetTexture(n); } catch (Exception) { }
                described.Add(n + "=" + (tx != null ? tx.name : "-"));
            }
            _props = string.Join(";", described.ToArray());
            SlotRows.Clear();
            for (int o = 0; o < slots.Length; o++)
            {
                int slot = slots[o];
                string ore = S8aMaterialMap.Subtypes[S8aMaterialMap.SurfaceMaterials + o];
                var row = new Dictionary<string, object> { { "slot", slot }, { "ore", ore } };
                if (t[slot] != null)
                {
                    Taken++;
                    row["taken"] = t[slot].name;
                    SlotRows.Add(row);
                    continue;
                }
                var mat = new Material(tpl.material) { name = "DE ore " + ore };
                int replaced = 0;
                if (on)
                {
                    Texture2D tex = OreTexture(o);
                    foreach (string n in names)
                    {
                        if (!Albedo(n, tpl.material)) continue;
                        try { mat.SetTexture(n, tex); replaced++; } catch (Exception) { }
                    }
                }
                VoxelandBlockType bt = VoxelandBlockType.FilledMaterial(mat, "DE_Ore_" + ore);
                bt.layer = tpl.layer;
                bt.RuntimeInit(slot);
                t[slot] = bt;
                Own[slot] = new Material(mat);
                Live[slot] = mat;
                Filled++;
                TexturesReplaced += replaced;
                row["texturesReplaced"] = replaced;
                SlotRows.Add(row);
            }
            _types = t;
            Applied = true;
            Look = "ours";
            Row();
            DeLog.Info("DE.S8a ore palette: " + Filled + " ore types in slots " + string.Join(",", Array.ConvertAll(slots, x => x.ToString(CultureInfo.InvariantCulture))) + " (template " +
                       TemplateName + ", " + TexturesReplaced + " textures replaced, " + Taken + " slots already the game's" + (on ? "" : ", textures off: " + EnvSwitch) + ")");
            return t;
        }

        /// <summary>An albedo-like texture property of the template: not a normal, mask, noise or effect map, by name and by its texture's name.</summary>
        private static bool Albedo(string prop, Material tpl)
        {
            string p = prop.ToLowerInvariant();
            foreach (string w in KeepWords) if (p.Contains(w)) return false;
            Texture tx = null;
            try { tx = tpl.GetTexture(prop); } catch (Exception) { }
            if (tx == null) return false;
            string tn = (tx.name ?? "").ToLowerInvariant();
            foreach (string w in KeepWords) if (tn.Contains(w)) return false;
            return tn.EndsWith("_n") == false;
        }

        // ------------------------------------------------------------------------------------------ the generated ore texture

        private static uint Hash(int x, int y, int seed)
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }

        /// <summary>Tileable value noise in [0, 1] (period cells per side).</summary>
        private static float Noise(float x, float y, int period, int seed)
        {
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            int ax = ((x0 % period) + period) % period, ay = ((y0 % period) + period) % period, bx = (ax + 1) % period, by = (ay + 1) % period;
            float v00 = (Hash(ax, ay, seed) & 0xffff) / 65535f, v10 = (Hash(bx, ay, seed) & 0xffff) / 65535f;
            float v01 = (Hash(ax, by, seed) & 0xffff) / 65535f, v11 = (Hash(bx, by, seed) & 0xffff) / 65535f;
            float a = v00 + fx * (v10 - v00), b = v01 + fx * (v11 - v01);
            return a + fy * (b - a);
        }

        private static float Fbm(float u, float v, int baseCells, int octaves, int seed)
        {
            float sum = 0f, amp = 0.5f, norm = 0f;
            int cells = baseCells;
            for (int o = 0; o < octaves; o++)
            {
                sum += amp * Noise(u * cells, v * cells, cells, seed + o * 7919);
                norm += amp;
                amp *= 0.5f;
                cells *= 2;
            }
            return sum / norm;
        }

        /// <summary>The ore's texture (deterministic per ore): dark speckled rock, a network of veins in the ore's colour (ridged noise), crystal glints.</summary>
        public static Texture2D OreTexture(int ore)
        {
            int n = TextureSize;
            var px = new Color32[n * n];
            Color32 c = OreColours[ore % OreColours.Length];
            int seed = 4546 + 101 * ore;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = x / (float)n, v = y / (float)n;
                    float rock = 0.16f + 0.14f * Fbm(u, v, 8, 4, seed);
                    float ridge = 1f - Math.Abs(2f * Fbm(u, v, 4, 3, seed + 31) - 1f);
                    // a test run (nl 20261024-134042, design 3's ore fields at 29.9 km read as rust): thinner, brighter veins and more glints, so
 // the far mip level stays the dark rock's tone with a sparkle instead of averaging into the ore's colour
                    float vein = Mathf.Clamp01((ridge - 0.88f) / 0.07f);
                    float ridge2 = 1f - Math.Abs(2f * Fbm(u, v, 6, 2, seed + 57) - 1f);
                    vein = Math.Max(vein, 0.7f * Mathf.Clamp01((ridge2 - 0.92f) / 0.05f));
                    float glint = (Hash(x, y, seed + 99) & 0xffff) / 65535f > 0.985f ? 1f : 0f;
                    float k = Math.Min(1f, vein + 0.8f * glint * Fbm(u, v, 16, 1, seed + 3));
                    float r = rock * (1f - k) + (c.r / 255f) * k, g = rock * (1f - k) + (c.g / 255f) * k, b = rock * (1f - k) + (c.b / 255f) * k;
                    px[y * n + x] = new Color32((byte)(Mathf.Clamp01(r) * 255f), (byte)(Mathf.Clamp01(g) * 255f), (byte)(Mathf.Clamp01(b) * 255f), 255);
                }
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, true) { name = "DE_ore_" + ore, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
            tex.SetPixels32(px);
            tex.Apply(true, false);
            return tex;
        }

        // ------------------------------------------------------------------------------------------ the paint test (section 13's fallback)

 /// <summary>
 /// Every ore material shows another palette type's material (its shader and properties copied in place: the meshes already built
 /// redraw with it), or ours again for "ours". Returns the look now, or an error.
 /// </summary>
        public static string LookAs(string what)
        {
            if (!Applied || _types == null) return "error: the ore palette is not applied";
            if (string.Equals(what, "ours", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var kv in Live)
                {
                    Material own = Own[kv.Key];
                    kv.Value.shader = own.shader;
                    kv.Value.CopyPropertiesFromMaterial(own);
                }
                Look = "ours";
                return Look;
            }
            int type;
            if (!int.TryParse(what, NumberStyles.Integer, CultureInfo.InvariantCulture, out type) || type <= 0 || type >= _types.Length || _types[type] == null || _types[type].material == null)
                return "error: no palette type " + what;
            Material src = _types[type].material;
            foreach (var kv in Live)
            {
                kv.Value.shader = src.shader;
                kv.Value.CopyPropertiesFromMaterial(src);
            }
            Look = type.ToString(CultureInfo.InvariantCulture) + " " + _types[type].name;
            return Look;
        }

        public static Dictionary<string, object> Status()
        {
            var parts = new List<string>();
            foreach (var r in SlotRows)
                parts.Add(r["slot"] + ":" + r["ore"] + ":" + (r.ContainsKey("taken") ? "taken " + r["taken"] : "filled " + r["texturesReplaced"]));
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "patched", Patched }, { "applied", Applied }, { "template", TemplateName }, { "filled", (long)Filled }, { "taken", (long)Taken },
                { "texturesReplaced", (long)TexturesReplaced }, { "look", Look }, { "slots", string.Join(";", parts.ToArray()) }, { "templateTextures", _props },
            };
        }

        private static void Row()
        {
            var j = new JsonOut().Str("t", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Num("frame", DeLog.Frame).Str("level", "info")
                .Str("event", "s8a.palette.ore").Str("ev", "s8a.palette.ore").Obj("data");
            foreach (var kv in Status())
            {
                object v = kv.Value;
                if (v is bool) j.Bool(kv.Key, (bool)v);
                else if (v is long) j.Num(kv.Key, (long)v);
                else j.Str(kv.Key, v as string);
            }
            S8aTerrain.Row(j.EndObj().ToString());
        }
    }
}
