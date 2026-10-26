using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Sandbox.Game.Entities;
using VRage.Game.Components;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// <c>de.
 /// live tuning of the Water Mod's look on the bound planet, written by version-gated reflection into the running
 /// <c>Jakaria.Components.WaterComponent</c> — <c>Settings</c> (Texture, Transparent, Lit, EnableFoam, WaveHeight,
 /// WaveSpeed, WaveScale, FogColor, Volumetric, MaterialId) and <c>PlanetConfig</c> (ColorIntensity, Specularity,
 /// SpecularIntensity, AmbientColorIntensity, the Distant* fields) — followed by <c>WaterRenderComponent.RebuildLOD</c>
 /// when the texture or transparency changed, so the new billboards take them. Single player only (the mod's settings
 /// are the server's). <c>reset</c> restores what was read before the first change.
 /// Data/WaterConfig.xml (new worlds) and to <see cref="Defaults"/>, applied at bind for existing worlds.
 /// Chat: /de water | /de water key=value … | /de water reset | /de water textures.
 /// WaveHeight moves the physical surface (GetDepth includes the wave): the S1/S2b contract pins 0
 /// tries are his call and are logged.
 /// </summary>
    internal static class WaterLook
    {
        public const string ChatUsage = "/de water [key=value …] | reset | textures  (keys: texture transparent lit foam waveHeight waveSpeed waveScale volumetric fog=r,g,b material colorIntensity specularity specularIntensity ambient distantRadiusOffset distantRadiusScaler distantSoftnessOffset distantSoftnessScaler distantSoftnessMultiplier)";

        /// <summary>The Water Mod 3.23 water textures (TransparentMaterials.sbc of the Workshop copy; JWaterOld is the mod's default).</summary>
        public static readonly string[] Textures = { "JWater", "JWater2", "JWaterFull", "JWaterFullLight", "JWaterDark", "JWaterLight", "JWaterOld", "XboxWater_cm" };

        /// <summary>
 /// empty = the world's own).
 /// <para>
 /// S7t T4 (
 /// screenshot pair of 23:05 (SE at +500 m, Subnautica at +380 m, mean sRGB of the same screen regions): near water SE
 /// (27,68,104) L62 vs Subnautica (65,97,122) L92; far water SE (38,72,96) L67 vs (129,155,187) L152. The Water Mod
 /// lights its surface billboards with <c>max(dot(up, sun), AmbientColorIntensity) × ColorIntensity</c> above water
 /// (VERIFIED mod source <c>WaterRenderSessionComponent</c>; <c>FogColor</c> applies only under water, where Site shows
 /// Subnautica's picture, so it is not a band field and stays the world's). Mapping:
 /// <list type="bullet">
 /// <item><c>colorIntensity</c> 5 → 8: the near-water luminance ratio is ×1.48 on screen (sRGB), ×2.4 in linear
 /// light; SE's eye adaptation answers a brighter sea that fills half the frame at the band, so ×1.6 (HYPOTHESIS: the
 /// <c>/de water colorIntensity=…</c>).</item>
 /// <item><c>ambient</c> 0.2 → 0.3: Subnautica's sea keeps its sky reflection at a low sun (Fresnel at grazing view),
 /// SE's billboards fall to the ambient floor.</item>
 /// <item><c>specularIntensity</c> 1 → 0.8: Subnautica's <c>WaterSurface.sunReflectionAmount</c> = 0.8
 /// <c>specularity</c> stays 4 (Subnautica's <c>sunReflectionGloss</c> 200 is another scale).</item>
 /// <item>texture JWater2, opaque, lit, no foam (Subnautica shows no foam from altitude), waves 0.5 m / 0.02 / 0.5:
 /// The hue difference (SE more saturated) and the far-water haze (Subnautica's
 /// aerial perspective) are T5's: SE's colour grade and atmosphere at the band.</item>
 /// </list>
 /// </para>
 /// </summary>
        public static readonly Dictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {

            // 0.5 m waves (contract WaveHeight 0 holds for the oracle sessions: DEEPENGINEERS_WATERLOOK=off), colour x5;
 // S7t T4: colour x8, ambient 0.3, specular intensity 0.8 (toward Subnautica's sea at the band; the remarks above)
            { "texture", "JWater2" }, { "transparent", "off" }, { "lit", "on" }, { "foam", "off" },
            { "waveHeight", "0.5" }, { "waveSpeed", "0.02" }, { "waveScale", "0.5" },
            { "colorIntensity", "8" }, { "specularity", "4" }, { "specularIntensity", "0" }, { "ambient", "0.3" },
            // S15 hook (the sea seam, CONTRACT 5.4 "S15 conventions", format water.material): specularIntensity 0.8 -> 0
 // (S15Conventions.Water.SpecularIntensity). The mod's specular is no glint: its half vector mixes planet-local and
 // world space and it switches on where sunDot > 0, a step along the quad edges
 // at the terminator (the S8a look sheet 20261024-124136); at the site's noon it added nothing.
        };

        private enum Kind { Bool, Float, Vector3, StringId, StringProperty }

        private sealed class Key
        {
            public readonly string Holder, Member;
            public readonly Kind Kind;
            public Key(string holder, string member, Kind kind) { Holder = holder; Member = member; Kind = kind; }
        }

        private static readonly Dictionary<string, Key> Keys = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase)
        {
            { "texture", new Key("settings", "Texture", Kind.StringId) },
            { "transparent", new Key("settings", "Transparent", Kind.Bool) },
            { "lit", new Key("settings", "Lit", Kind.Bool) },
            { "foam", new Key("settings", "EnableFoam", Kind.Bool) },
            { "waveHeight", new Key("settings", "WaveHeight", Kind.Float) },
            { "waveSpeed", new Key("settings", "WaveSpeed", Kind.Float) },
            { "waveScale", new Key("settings", "WaveScale", Kind.Float) },
            { "volumetric", new Key("settings", "Volumetric", Kind.Bool) },
            { "fog", new Key("settings", "FogColor", Kind.Vector3) },
            { "material", new Key("settings", "MaterialId", Kind.StringProperty) },
            { "colorIntensity", new Key("planet", "ColorIntensity", Kind.Float) },
            { "specularity", new Key("planet", "Specularity", Kind.Float) },
            { "specularIntensity", new Key("planet", "SpecularIntensity", Kind.Float) },
            { "ambient", new Key("planet", "AmbientColorIntensity", Kind.Float) },
            { "distantRadiusOffset", new Key("planet", "DistantRadiusOffset", Kind.Float) },
            { "distantRadiusScaler", new Key("planet", "DistantRadiusScaler", Kind.Float) },
            { "distantSoftnessOffset", new Key("planet", "DistantSoftnessOffset", Kind.Float) },
            { "distantSoftnessScaler", new Key("planet", "DistantSoftnessScaler", Kind.Float) },
            { "distantSoftnessMultiplier", new Key("planet", "DistantSoftnessMultiplier", Kind.Float) },
        };

        private static readonly string[] Order =
        {
            "texture", "transparent", "lit", "foam", "waveHeight", "waveSpeed", "waveScale", "volumetric", "fog", "material",
            "colorIntensity", "specularity", "specularIntensity", "ambient", "distantRadiusOffset", "distantRadiusScaler",
            "distantSoftnessOffset", "distantSoftnessScaler", "distantSoftnessMultiplier",
        };

        private static readonly Dictionary<string, object> _originals = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        public static int Changes;

        public static Dictionary<string, object> Command(IDictionary<string, object> args, string via)
        {
            if (args != null && MiniJson.GetBool(args, "textures") == true)
                return Ok("DE.Water textures: " + string.Join(", ", Textures) + " (the mod's default is JWaterOld)");
            object comp, settings;
            string why = null;
            MyPlanet planet = SiteBinding.Planet;
            WaterModClient api = SiteBinding.Api;
            if (api == null || !api.TryGetWaterComponent(planet, out comp, out settings, out why)) return Fail("de.water: " + (why ?? "Water Mod not reachable"));
            object planetConfig = null;
            try { FieldInfo pf = comp.GetType().GetField("PlanetConfig", BindingFlags.Public | BindingFlags.Instance); if (pf != null) planetConfig = pf.GetValue(comp); }
            catch (Exception) { }
            var applied = new List<string>();
            var problems = new List<string>();
            bool rebuild = false;
            if (args != null && MiniJson.GetBool(args, "reset") == true)
            {
                foreach (var kv in _originals)
                {
                    Key k;
                    if (!Keys.TryGetValue(kv.Key, out k)) continue;
                    string err = SetRaw(k, settings, planetConfig, kv.Value);
                    if (err != null) problems.Add(kv.Key + ": " + err); else applied.Add(kv.Key + "=" + Fmt(kv.Value));
                    if (k.Kind == Kind.StringId || k.Member == "Transparent" || k.Kind == Kind.StringProperty) rebuild = true;
                }
                _originals.Clear();
            }
            else if (args != null)
            {
                foreach (var kv in args)
                {
                    Key k;
                    if (!Keys.TryGetValue(kv.Key, out k)) { if (kv.Key != "via" && kv.Key != "argv") problems.Add("unknown key '" + kv.Key + "'"); continue; }
                    object value;
                    string perr = Parse(k, kv.Value, out value);
                    if (perr != null) { problems.Add(kv.Key + ": " + perr); continue; }
                    if (k.Kind == Kind.StringId && Array.IndexOf(Textures, (string)value) < 0) problems.Add("texture '" + value + "' is not a known water texture (applied anyway)");
                    object before;
                    string gerr = GetRaw(k, settings, planetConfig, out before);
                    if (gerr != null) { problems.Add(kv.Key + ": " + gerr); continue; }
                    if (!_originals.ContainsKey(kv.Key)) _originals[kv.Key] = before;
                    string err = SetRaw(k, settings, planetConfig, value);
                    if (err != null) { problems.Add(kv.Key + ": " + err); continue; }
                    applied.Add(kv.Key + ": " + Fmt(before) + " -> " + Fmt(value));
                    Changes++;
                    if (k.Kind == Kind.StringId || k.Member == "Transparent" || k.Kind == Kind.StringProperty) rebuild = true;
                }
            }
            string rebuilt = rebuild ? RebuildLod(comp, planet) : null;
            string state = State(settings, planetConfig);
            var sb = new StringBuilder("DE.Water ");
            if (applied.Count > 0) sb.Append("applied ").Append(string.Join(", ", applied.ToArray())).Append("; ");
            if (rebuilt != null) sb.Append(rebuilt).Append("; ");
            if (problems.Count > 0) sb.Append("problems: ").Append(string.Join("; ", problems.ToArray())).Append("; ");
            sb.Append("now ").Append(state).Append(" (via ").Append(via).Append(')');
            string line = sb.ToString();
            DeLog.Info(line);
            Dictionary<string, object> r = Ok(line);
            r["state"] = state;
            r["changes"] = Changes;
            if (problems.Count > 0) r["problems"] = problems;
            return r;
        }

        /// <summary>Applies <see cref="Defaults"/> (bind time; existing worlds keep their spawn-time config otherwise).</summary>
        public static void ApplyDefaults(string via)
        {
            if (Defaults.Count == 0) return;
            string env = Environment.GetEnvironmentVariable("DEEPENGINEERS_WATERLOOK");
            if (env != null && env.Trim().Equals("off", StringComparison.OrdinalIgnoreCase)) { DeLog.Info("DE.Water defaults skipped (DEEPENGINEERS_WATERLOOK=off)"); return; }
            var args = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var kv in Defaults) args[kv.Key] = kv.Value;
            try { Command(args, via); } catch (Exception e) { DeLog.Info("DE.Water defaults not applied: " + e.GetType().Name + ": " + e.Message); }
        }

        // ------------------------------------------------------------------------------------------------- reflection

        private static object Holder(Key k, object settings, object planetConfig) { return k.Holder == "planet" ? planetConfig : settings; }

        private static string GetRaw(Key k, object settings, object planetConfig, out object value)
        {
            value = null;
            object h = Holder(k, settings, planetConfig);
            if (h == null) return k.Holder + " object missing";
            try
            {
                if (k.Kind == Kind.StringProperty)
                {
                    PropertyInfo p = h.GetType().GetProperty(k.Member, BindingFlags.Public | BindingFlags.Instance);
                    if (p == null) return "property " + k.Member + " not found";
                    value = p.GetValue(h, null);
                    return null;
                }
                FieldInfo f = h.GetType().GetField(k.Member, BindingFlags.Public | BindingFlags.Instance);
                if (f == null) return "field " + k.Member + " not found";
                object raw = f.GetValue(h);
                if (k.Kind == Kind.StringId)
                {
                    PropertyInfo ps = f.FieldType.GetProperty("String", BindingFlags.Public | BindingFlags.Instance);
                    value = ps != null && raw != null ? ps.GetValue(raw, null) : raw;
                    return null;
                }
                value = raw;
                return null;
            }
            catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
        }

        private static string SetRaw(Key k, object settings, object planetConfig, object value)
        {
            object h = Holder(k, settings, planetConfig);
            if (h == null) return k.Holder + " object missing";
            try
            {
                if (k.Kind == Kind.StringProperty)
                {
                    PropertyInfo p = h.GetType().GetProperty(k.Member, BindingFlags.Public | BindingFlags.Instance);
                    if (p == null || !p.CanWrite) return "property " + k.Member + " not writable";
                    p.SetValue(h, value as string, null);
                    return null;
                }
                FieldInfo f = h.GetType().GetField(k.Member, BindingFlags.Public | BindingFlags.Instance);
                if (f == null) return "field " + k.Member + " not found";
                switch (k.Kind)
                {
                    case Kind.Bool: if (f.FieldType != typeof(bool)) return "not a bool"; f.SetValue(h, (bool)value); return null;
                    case Kind.Float: if (f.FieldType != typeof(float)) return "not a float"; f.SetValue(h, (float)value); return null;
                    case Kind.Vector3: if (f.FieldType != typeof(Vector3)) return "not a Vector3"; f.SetValue(h, (Vector3)value); return null;
                    case Kind.StringId:
                    {
                        object boxed = f.GetValue(h);
                        PropertyInfo ps = f.FieldType.GetProperty("String", BindingFlags.Public | BindingFlags.Instance);
                        if (boxed == null || ps == null || !ps.CanWrite) return "SerializableStringId.String not writable";
                        ps.SetValue(boxed, (string)value, null);
                        f.SetValue(h, boxed);
                        return null;
                    }
                }
                return "unsupported kind";
            }
            catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
        }

        private static string RebuildLod(object comp, MyPlanet planet)
        {
            try
            {
                Type rt = comp.GetType().Assembly.GetType("Jakaria.Components.WaterRenderComponent", false);
                if (rt == null) return "render component type not found (texture takes effect on the next LOD rebuild)";
                MyComponentBase rc;
                if (!planet.Components.TryGet(rt, out rc) || rc == null) return "no WaterRenderComponent on the planet";
                MethodInfo m = rt.GetMethod("RebuildLOD", BindingFlags.Public | BindingFlags.Instance);
                if (m == null) return "RebuildLOD not found";
                m.Invoke(rc, null);
                return "render LOD rebuilt";
            }
            catch (Exception e) { return "RebuildLOD failed: " + e.GetType().Name + ": " + e.Message; }
        }

        private static string State(object settings, object planetConfig)
        {
            var sb = new StringBuilder();
            foreach (string name in Order)
            {
                object v;
                string err = GetRaw(Keys[name], settings, planetConfig, out v);
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(name).Append('=').Append(err == null ? Fmt(v) : "?");
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------------------------------------- values

        private static string Parse(Key k, object raw, out object value)
        {
            value = null;
            string s = raw as string;
            switch (k.Kind)
            {
                case Kind.Bool:
                {
                    if (raw is bool) { value = (bool)raw; return null; }
                    if (raw is double) { value = (double)raw != 0; return null; }
                    if (s == null) return "needs on|off";
                    string t = s.Trim().ToLowerInvariant();
                    if (t == "on" || t == "true" || t == "1" || t == "yes") { value = true; return null; }
                    if (t == "off" || t == "false" || t == "0" || t == "no") { value = false; return null; }
                    return "needs on|off";
                }
                case Kind.Float:
                {
                    double d;
                    if (raw is double) d = (double)raw;
                    else if (raw is long) d = (long)raw;
                    else if (raw is int) d = (int)raw;
                    else if (s != null && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) { }
                    else return "needs a number";
                    if (double.IsNaN(d) || double.IsInfinity(d)) return "needs a finite number";
                    value = (float)d;
                    return null;
                }
                case Kind.Vector3:
                {
                    var parts = new List<double>();
                    var list = raw as IList<object>;
                    if (list != null) foreach (object o in list) { if (o is double) parts.Add((double)o); else return "needs r,g,b"; }
                    else if (s != null) foreach (string p in s.Split(',', ' ')) { double d; if (p.Trim().Length == 0) continue; if (!double.TryParse(p.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return "needs r,g,b"; parts.Add(d); }
                    if (parts.Count != 3) return "needs r,g,b (0..1)";
                    value = new Vector3((float)parts[0], (float)parts[1], (float)parts[2]);
                    return null;
                }
                case Kind.StringId:
                case Kind.StringProperty:
                    if (string.IsNullOrEmpty(s)) return "needs a name";
                    value = s.Trim();
                    return null;
            }
            return "unsupported";
        }

        private static string Fmt(object v)
        {
            if (v == null) return "null";
            if (v is float) return ((float)v).ToString("0.###", CultureInfo.InvariantCulture);
            if (v is double) return ((double)v).ToString("0.###", CultureInfo.InvariantCulture);
            if (v is bool) return (bool)v ? "on" : "off";
            if (v is Vector3) { var c = (Vector3)v; return c.X.ToString("0.###", CultureInfo.InvariantCulture) + "," + c.Y.ToString("0.###", CultureInfo.InvariantCulture) + "," + c.Z.ToString("0.###", CultureInfo.InvariantCulture); }
            return v.ToString();
        }

        private static Dictionary<string, object> Ok(string line)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", true }, { "line", line } };
        }

        private static Dictionary<string, object> Fail(string error)
        {
            DeLog.Info("DE.Dev " + error);
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", false }, { "error", error }, { "line", error } };
        }
    }
}
