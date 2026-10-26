using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>The arguments of <c>de.spawnplanet</c> after parsing (defaults from SiteV1 and tools/worldmod/check_planet.py).</summary>
    internal struct SpawnRequest
    {
        public string Definition;
        /// <summary>Spawn size = diameter in metres (the spawn menu's "Select Size"); the storage radius is size / 2.</summary>
        public float Size;
        public int Seed;
        /// <summary>The planet centre C wanted (what <c>PositionComp.GetPosition</c> must read after the spawn).</summary>
        public Vec3 Centre;
        /// <summary>Validate and compute everything, but do not call AddPlanet.</summary>
        public bool DryRun;
    }

    /// <summary>
 /// Game-independent part of <c>de.spawnplanet</c> (tests/DeepEngineers.SE.Tests): argument parsing (C11 JSON and the
 /// <c>/de spawnplanet</c> chat form), the storage name, the octree storage size and the min-corner rule. Every rule is
 /// read (never copied):
 /// <list type="bullet">
 /// <item>storage name: <c>MyGuiScreenDebugSpawnMenu.SpawnPlanet_Server</c> and <c>MyVoxelMaps.SpawnPlanet</c> both
 /// use <c>planetName + "-" + seed + "d" + size</c>;</item>
 /// <item>storage size: <c>MyWorldGenerator.CreatePlanet</c> → <c>MyPlanetStorageProvider.Init(seed, def, size / 2f)</c>
 /// → radius r = max(size/2, 1), <c>StorageSize = MyVoxelCoordSystems.FindBestOctreeSize(2f * (r + r * HillParams.Max))</c>,
 /// the first power of two from 32 that is ≥ its argument (float arithmetic);</item>
 /// <item>centre: <c>MyVoxelBase.Init(storageName, storage, positionMinCorner)</c> builds the world matrix at
 /// <c>positionMinCorner + storage.Size / 2</c> (Vector3I integer division), then <c>InitVoxelMap</c> adds 0.5 on
 /// each axis when <c>MyPerGameSettings.OffsetVoxelMapByHalfVoxel</c> (set true by <c>SpaceEngineersGame</c>).</item>
 /// </list>
 /// So C = minCorner + StorageSize/2 + 0.5 and minCorner = C − StorageSize/2 − 0.5: for DE_4546B (size 120000,
 /// HillParams.Max 0.12, storage 262,144) and C = (−1000000, 250000, 1500000) the min corner is
 /// (−1131072.5, 118927.5, 1368927.5), as <c>check_planet.py</c> prints.
 /// </summary>
    internal static class LabSpawn
    {
        public const string DefaultDefinition = SiteV1.GeneratorSubtypeId;
        public const float DefaultSize = 120000f;
        public const int DefaultSeed = 4546;
        /// <summary><c>MyPlanets.MAX_PLANET_SIZE</c>.</summary>
        public const float MaxSize = 8000000f;
        /// <summary>The lab guide's rule (DE_LAB_WORLD.md §2): no other planet centre within 200 km of C.</summary>
        public const double MinOtherPlanetDistanceM = 200000.0;
        /// <summary>The keys <c>de.spawnplanet</c> accepts (anything else is refused: a typo must not fall back to a default).</summary>
        public static readonly string[] Keys = { "definition", "size", "seed", "centre", "center", "dryRun" };

        public const string ChatUsage = "/de spawnplanet [dry] [definition=DE_4546B] [size=120000] [seed=4546] [centre=-1000000,250000,1500000]";

        public static Vec3 DefaultCentre { get { return SiteV1.TestC; } }

        // ============================================================================================ arguments

 /// <summary>C11 args → request; false with the reason when an argument is unknown, missing its type or out of range.</summary>
        public static bool TryParse(IDictionary<string, object> args, out SpawnRequest r, out string error)
        {
            r = new SpawnRequest { Definition = DefaultDefinition, Size = DefaultSize, Seed = DefaultSeed, Centre = DefaultCentre, DryRun = false };
            error = null;
            if (args == null) return true;
            foreach (string key in args.Keys)
            {
                if (Array.IndexOf(Keys, key) < 0)
                {
                    error = "unknown argument '" + key + "' (accepted: " + string.Join(", ", Keys) + ")";
                    return false;
                }
            }
            object v;
            if (args.TryGetValue("definition", out v) && v != null)
            {
                string d = v as string;
                if (string.IsNullOrEmpty(d) || d.Length > 128 || d.Trim() != d) { error = "definition must be a planet generator SubtypeId string"; return false; }
                r.Definition = d;
            }
            if (args.TryGetValue("size", out v) && v != null)
            {
                double s;
                if (!AsNumber(v, out s) || !(s >= 1.0) || s > MaxSize) { error = "size must be a number in [1, " + Num(MaxSize) + "] (diameter, metres)"; return false; }
                r.Size = (float)s;
            }
            if (args.TryGetValue("seed", out v) && v != null)
            {
                double s;
                if (!AsNumber(v, out s) || s != Math.Floor(s) || s < int.MinValue || s > int.MaxValue) { error = "seed must be an integer (int32)"; return false; }
                r.Seed = (int)s;
            }
            object c = null;
            if (args.TryGetValue("centre", out v) && v != null) c = v;
            if (args.TryGetValue("center", out v) && v != null)
            {
                if (c != null) { error = "give either centre or center, not both"; return false; }
                c = v;
            }
            if (c != null)
            {
                Vec3 centre;
                if (!AsVec3(c, out centre)) { error = "centre must be [x, y, z] (or {\"x\", \"y\", \"z\"}) of finite numbers, SE world metres"; return false; }
                r.Centre = centre;
            }
            if (args.TryGetValue("dryRun", out v) && v != null)
            {
                if (!(v is bool)) { error = "dryRun must be true or false"; return false; }
                r.DryRun = (bool)v;
            }
            return true;
        }

        /// <summary>
 /// The chat form (<see cref="ChatUsage"/>): tokens after <paramref name="first"/> → C11 args. False with the usage
 /// text when a token is not understood.
 /// </summary>
        public static bool ParseChat(string[] argv, int first, Dictionary<string, object> args, out string usage)
        {
            usage = null;
            for (int i = first; i < argv.Length; i++)
            {
                string t = argv[i];
                if (t.Equals("dry", StringComparison.OrdinalIgnoreCase) || t.Equals("dryrun", StringComparison.OrdinalIgnoreCase))
                {
                    args["dryRun"] = true;
                    continue;
                }
                int eq = t.IndexOf('=');
                if (eq <= 0 || eq == t.Length - 1) { usage = ChatUsage; return false; }
                string key = t.Substring(0, eq).ToLowerInvariant(), val = t.Substring(eq + 1);
                double d;
                switch (key)
                {
                    case "definition":
                    case "def":
                        args["definition"] = val;
                        break;
                    case "size":
                    case "seed":
                        if (!double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) { usage = ChatUsage; return false; }
                        args[key] = d;
                        break;
                    case "centre":
                    case "center":
                    {
                        string[] p = val.Split(',');
                        if (p.Length != 3) { usage = ChatUsage; return false; }
                        var list = new List<object>(3);
                        foreach (string s in p)
                        {
                            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) { usage = ChatUsage; return false; }
                            list.Add(d);
                        }
                        args["centre"] = list;
                        break;
                    }
                    default:
                        usage = ChatUsage;
                        return false;
                }
            }
            return true;
        }

        // ============================================================================================== geometry

 /// <summary>The spawn menu's storage name: "DE_4546B-4546d120000" (numbers formatted invariant).</summary>
        public static string StorageName(string definition, int seed, float size)
        {
            return definition + "-" + seed.ToString(CultureInfo.InvariantCulture) + "d" + size.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
 /// <c>MyPlanetStorageProvider.StorageSize.X</c> for a spawn size and the generator's <c>HillParams.Max</c>
 /// (float arithmetic as the game; see the class remarks). 262,144 for DE_4546B at 120000.
 /// </summary>
        public static int StorageSize(float size, float hillMax)
        {
            double radius = Math.Max((double)(size / 2f), 1.0); // Init(seed, def, double radius): Math.Max(radius, 1.0)
            float r = (float)radius;
            float maxHill = r * hillMax;
            float outer = r + maxHill;
            float target = 2f * outer;
            int n = 32;
            while ((float)n < target)
            {
                if (n >= 1 << 30) break; // never in range: MAX_PLANET_SIZE keeps target far below
                n *= 2;
            }
            return n;
        }

        /// <summary>The half-voxel offset SE adds to every voxel map's world matrix (0.5 when <c>OffsetVoxelMapByHalfVoxel</c>).</summary>
        public static double HalfVoxel(bool offsetVoxelMapByHalfVoxel)
        {
            return offsetVoxelMapByHalfVoxel ? 0.5 : 0.0;
        }

        /// <summary>The <c>positionMinCorner</c> to pass to <c>MyWorldGenerator.AddPlanet</c> so the planet centre is <paramref name="centre"/>.</summary>
        public static Vec3 MinCornerFromCentre(Vec3 centre, int storageSize, bool offsetVoxelMapByHalfVoxel)
        {
            double k = (storageSize / 2) + HalfVoxel(offsetVoxelMapByHalfVoxel); // Vector3I / 2: integer division
            return new Vec3(centre.X - k, centre.Y - k, centre.Z - k);
        }

        /// <summary>The centre SE computes from a min corner (the forward rule; the inverse of <see cref="MinCornerFromCentre"/>).</summary>
        public static Vec3 CentreFromMinCorner(Vec3 minCorner, int storageSize, bool offsetVoxelMapByHalfVoxel)
        {
            double k = (storageSize / 2) + HalfVoxel(offsetVoxelMapByHalfVoxel);
            return new Vec3(minCorner.X + k, minCorner.Y + k, minCorner.Z + k);
        }

        /// <summary>
 /// The other planet nearest to <paramref name="centre"/> when its centre is closer than
 /// <see cref="MinOtherPlanetDistanceM"/>, else null. Each entry: (label, centre).
 /// </summary>
        public static string TooClosePlanet(IList<KeyValuePair<string, Vec3>> planets, Vec3 centre, out double distance)
        {
            distance = double.PositiveInfinity;
            string label = null;
            if (planets == null) return null;
            foreach (var p in planets)
            {
                double d = (p.Value - centre).Length;
                if (d < distance) { distance = d; label = p.Key; }
            }
            return distance < MinOtherPlanetDistanceM ? label : null;
        }

        // ================================================================================================ helpers

        private static bool AsNumber(object v, out double d)
        {
            d = double.NaN;
            if (v is double) d = (double)v;
            else if (v is float) d = (float)v;
            else if (v is int) d = (int)v;
            else if (v is long) d = (long)v;
            else return false;
            return !double.IsNaN(d) && !double.IsInfinity(d);
        }

        private static bool AsVec3(object v, out Vec3 r)
        {
            r = Vec3.Zero;
            double x, y, z;
            var list = v as IList<object>;
            if (list != null)
            {
                if (list.Count != 3 || !AsNumber(list[0], out x) || !AsNumber(list[1], out y) || !AsNumber(list[2], out z)) return false;
                r = new Vec3(x, y, z);
                return true;
            }
            var o = v as IDictionary<string, object>;
            if (o != null)
            {
                object ox, oy, oz;
                if (o.Count != 3 || !o.TryGetValue("x", out ox) || !o.TryGetValue("y", out oy) || !o.TryGetValue("z", out oz)) return false;
                if (!AsNumber(ox, out x) || !AsNumber(oy, out y) || !AsNumber(oz, out z)) return false;
                r = new Vec3(x, y, z);
                return true;
            }
            return false;
        }

        public static string Num(double v)
        {
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>"(x, y, z)" with round-trip doubles.</summary>
        public static string V(Vec3 v)
        {
            return "(" + Num(v.X) + ", " + Num(v.Y) + ", " + Num(v.Z) + ")";
        }
    }

    // ===================================================================================================== G2-E3

    internal enum G2E3Status
    {
        Pass = 0,
        Fail = 1,
        /// <summary>Not computable from the game API (needs a rig or the SE log): reported with the reason.</summary>
        Manual = 2,
    }

    /// <summary>One row of <c>de.g2e3</c>: check id (guide §8 number + name), value, expected, status, note.</summary>
    internal struct G2E3Row
    {
        public string Check;
        public string Value;
        public string Expected;
        public G2E3Status Status;
        public string Note;
        /// <summary>For a MANUAL row: what it needs instead of the API ("rig", "log").</summary>
        public string ManualKind;

        public string StatusName
        {
            get { return Status == G2E3Status.Pass ? "PASS" : Status == G2E3Status.Fail ? "FAIL" : "MANUAL"; }
        }

        /// <summary>
 /// <c>DE.G2E3 &lt;check&gt; &lt;value&gt; &lt;expected&gt; PASS|FAIL (note)</c>, or <c>… MANUAL (rig) reason</c>;
 /// value and expected hold no spaces.
 /// </summary>
        public string Line
        {
            get
            {
                string s = "DE.G2E3 " + Check + " " + Value + " " + Expected + " " + StatusName;
                if (Status == G2E3Status.Manual) return s + " (" + (ManualKind ?? "manual") + ")" + (string.IsNullOrEmpty(Note) ? "" : " " + Note);
                if (!string.IsNullOrEmpty(Note)) s += " (" + Note + ")";
                return s;
            }
        }
    }

    /// <summary>
 /// What <c>de.g2e3</c> read from the game (LabCommands fills it; null = could not be read). The ten checks of
 /// docs/worldmod/DE_LAB_WORLD.md §8, without rigs.
 /// </summary>
    internal sealed class G2E3Facts
    {
        public int PlanetCount;
        /// <summary>DE_4546B <c>PositionComp.GetPosition</c>.</summary>
        public Vec3? Centre;
        public double? MinimumRadius, AverageRadius, MaximumRadius;
        /// <summary>Sea radius bits from the Water Mod API (<c>GetPhysicalData(planet).Item2</c>).</summary>
        public uint? RsBits;
        /// <summary>Water Mod settings by version-gated reflection (null when unreadable; <see cref="SettingsError"/> says why).</summary>
        public uint? SettingsRadiusBits;
        public float? WaveHeight, TideHeight, CrushDamage, CurrentSpeed;
        public bool? EnableFish, Volumetric;
        public string SettingsError;
        /// <summary><c>TryGetPlanetElevation(Sealevel)</c> at the water point: |P − C| − AverageRadius (MyShipController).</summary>
        public double? SeaLevelElevation;
        /// <summary>|natural gravity| (m/s²) at the water, 1,000 m and 2,000 m below it, along the anchor direction.</summary>
        public double? GravityWater, GravityMinus1000, GravityMinus2000;
        public double? AirDensity;
        /// <summary>Check 7: <c>MyOxygenProviderSystem.GetOxygenInPoint</c> at the water (what an unsealed air vent reads).</summary>
        public double? Oxygen;
        /// <summary>The DE_4546B's own share, <c>MyPlanet.GetOxygenForPosition</c> at the water (noted on row 7).</summary>
        public double? OxygenPlanet;
        /// <summary>Atmospheric thruster force multiplier at the water (stock small atmospheric thruster definition).</summary>
        public double? AtmoThrust;
        public string AtmoThrustDetail;
        /// <summary>Floor depth below the water (|S − C| − Rs) at three places; labels name them.</summary>
        public readonly string[] FloorLabels = { "anchor", "tileCorner", "faceX" };
        public readonly double?[] Floor = new double?[3];
        /// <summary>Voxel material just below the floor at the anchor (SubtypeId), or null.</summary>
        public string FloorMaterial;
        public string FloorMaterialDetail;
        /// <summary>Rs could not be read: the sea points use SiteV1.RsExpected (noted on the rows).</summary>
        public bool RsAssumed;
    }

    /// <summary>
 /// The S1.G2-E3 decision table without rigs (docs/worldmod/DE_LAB_WORLD.md §8; game-independent, tested with fake
 /// facts). Each row is PASS or FAIL against the guide's pass value; a value that could not be read is FAIL
 /// ("unreadable"); checks 8 (crush on a rig after 60 s) and 10 (SE log errors) are MANUAL with the reason.
 /// </summary>
    internal static class G2E3
    {
        public const double CentreTolM = 0.001;
        public const double AverageRadiusTolM = 0.001;
        public const double HillRadiusTolM = 0.01;
        public const double SeaLevelTolM = 0.05;
        public const double Gravity = 9.81;
        public const double GravityTol = 0.001;
        public const double AirDensityMin = 0.999;
        public const double AtmoThrustMin = 0.999;
        public const double Oxygen = 0.9;
        public const double OxygenTol = 0.001;
        public const double FloorM = -3000.0;
        public const double FloorTolM = 0.05;
        public const string FloorMaterial = "Sand_02";

        public static List<G2E3Row> Evaluate(G2E3Facts f)
        {
            var rows = new List<G2E3Row>(28);
            string rsNote = f.RsAssumed ? "Rs unreadable: SiteV1 RsExpected used for the sea points" : null;

            rows.Add(Row("0.planetCount", f.PlanetCount.ToString(CultureInfo.InvariantCulture), "1", f.PlanetCount == 1,
                         "planets whose generator is " + SiteV1.GeneratorSubtypeId));

            // 1. planet centre
            if (f.Centre.HasValue)
            {
                Vec3 d = f.Centre.Value - SiteV1.TestC;
                double worst = Math.Max(Math.Abs(d.X), Math.Max(Math.Abs(d.Y), Math.Abs(d.Z)));
                rows.Add(Row("1.centre", P(f.Centre.Value), P(SiteV1.TestC) + "+-" + G(CentreTolM), worst <= CentreTolM,
                             "PositionComp.GetPosition(); max axis error " + G(worst) + " m"));
            }
            else rows.Add(Missing("1.centre", P(SiteV1.TestC) + "+-" + G(CentreTolM)));

            // 2. radii
            rows.Add(Near("2.radius.average", f.AverageRadius, SiteV1.AverageRadius, AverageRadiusTolM, "MyPlanet.AverageRadius"));
            rows.Add(Near("2.radius.minimum", f.MinimumRadius, SiteV1.MinimumRadius, HillRadiusTolM, "MyPlanet.MinimumRadius: the flat floor"));
            rows.Add(Near("2.radius.maximum", f.MaximumRadius, SiteV1.MaximumRadius, HillRadiusTolM, "MyPlanet.MaximumRadius"));

            // 3. the sea (Water Mod API and settings)
            string sErr = f.SettingsError != null ? "settings unreadable: " + f.SettingsError : null;
            rows.Add(Bits("3.sea.radius", f.SettingsRadiusBits, SiteV1.WaterSettingsRadiusBits, sErr ?? "WaterSettings.Radius"));
            rows.Add(Bits("3.sea.Rs", f.RsBits, SiteV1.RsExpectedBits, "GetPhysicalData(planet).Item2 (S1.G2-E4)"));
            rows.Add(ZeroF("3.sea.waveHeight", f.WaveHeight, sErr));
            rows.Add(ZeroF("3.sea.tideHeight", f.TideHeight, sErr));
            rows.Add(ZeroF("3.sea.crushDamage", f.CrushDamage, sErr));
            rows.Add(ZeroF("3.sea.currentSpeed", f.CurrentSpeed, sErr));
            rows.Add(FalseB("3.sea.enableFish", f.EnableFish, sErr ?? "WaterSettings.EnableFish not found by reflection"));
            rows.Add(FalseB("3.sea.volumetric", f.Volumetric, sErr));

            // 4. SE sea level at the water
            rows.Add(Near("4.seaLevel", f.SeaLevelElevation, 0.0, SeaLevelTolM,
                          Join("TryGetPlanetElevation(Sealevel) = |P - C| - AverageRadius at P = C + U0*Rs (MyShipController formula; not a floating hull)", rsNote)));

            // 5. gravity
            rows.Add(Near("5.gravity.water", f.GravityWater, Gravity, GravityTol, Join("|CalculateNaturalGravityInPoint(C + U0*Rs)| m/s2", rsNote)));
            rows.Add(Near("5.gravity.-1000", f.GravityMinus1000, Gravity, GravityTol, Join("|g| at C + U0*(Rs - 1000)", rsNote)));
            rows.Add(Near("5.gravity.-2000", f.GravityMinus2000, Gravity, GravityTol, Join("|g| at C + U0*(Rs - 2000)", rsNote)));

            // 6. atmosphere, 7. oxygen
            rows.Add(AtLeast("6.airDensity", f.AirDensity, AirDensityMin, Join("MyPlanet.GetAirDensity at the water", rsNote)));
            rows.Add(AtLeast("6.atmoThrust", f.AtmoThrust, AtmoThrustMin, f.AtmoThrustDetail ?? "atmospheric thruster force multiplier at the water"));
            rows.Add(Near("7.oxygen", f.Oxygen, Oxygen, OxygenTol,
                          Join("MyOxygenProviderSystem.GetOxygenInPoint at the water (an unsealed air vent's reading); planet share " +
                               (f.OxygenPlanet.HasValue ? G(f.OxygenPlanet.Value) : "unreadable") + " (MyPlanet.GetOxygenForPosition)", rsNote)));

            // 8. crush on a rig: not an API value
            rows.Add(new G2E3Row
            {
                Check = "8.crush", Value = "-", Expected = "no-integrity-loss-after-60s", Status = G2E3Status.Manual, ManualKind = "rig",
                Note = "the Water Mod damages blocks that border an airtight room under water; only a grid held at -1000 m for 60 s shows it (guide §7 Rig B); the setting it applies is row 3.sea.crushDamage",
            });

            // 9. floor
            for (int i = 0; i < f.Floor.Length; i++)
                rows.Add(Near("9.floor." + f.FloorLabels[i], f.Floor[i], FloorM, FloorTolM, Join("|GetClosestSurfacePointGlobal(C + d*Rs) - C| - Rs", rsNote)));
            if (f.FloorMaterial != null)
                rows.Add(Row("9.floor.material", f.FloorMaterial, FloorMaterial, f.FloorMaterial == FloorMaterial,
                             f.FloorMaterialDetail ?? "voxel material just below the floor at the anchor"));
            else rows.Add(Missing("9.floor.material", FloorMaterial, f.FloorMaterialDetail));

            // 10. load errors: in the SE log only
            rows.Add(new G2E3Row
            {
                Check = "10.loadErrors", Value = "-", Expected = "none", Status = G2E3Status.Manual, ManualKind = "log",
                Note = "the heightmap/texture/material load errors are SE log lines (guide §4 step 7), not API values: grep the SE log",
            });
            return rows;
        }

        /// <summary><c>DE.G2E3 SUMMARY n PASS m FAIL k MANUAL</c>.</summary>
        public static string Summary(List<G2E3Row> rows, out int pass, out int fail, out int manual)
        {
            pass = fail = manual = 0;
            foreach (G2E3Row r in rows)
            {
                if (r.Status == G2E3Status.Pass) pass++;
                else if (r.Status == G2E3Status.Fail) fail++;
                else manual++;
            }
            var sb = new StringBuilder("DE.G2E3 SUMMARY ");
            sb.Append(pass).Append(" PASS ").Append(fail).Append(" FAIL ").Append(manual).Append(" MANUAL");
            if (fail > 0)
            {
                sb.Append(" (failed:");
                foreach (G2E3Row r in rows) if (r.Status == G2E3Status.Fail) sb.Append(' ').Append(r.Check);
                sb.Append(')');
            }
            return sb.ToString();
        }

        /// <summary>
 /// The atmospheric thruster force multiplier SE applies
 ///.
 /// </summary>
        public static double ThrustMultiplier(float influence, bool inAtmosphere, bool needsAtmosphere, float minInfluence, float maxInfluence,
                                              float invDiffMinMax, float effectivenessAtMin, float effectivenessAtMax)
        {
            if (needsAtmosphere && !inAtmosphere) return effectivenessAtMin;
            if (maxInfluence != minInfluence)
            {
                float t = (influence - minInfluence) * invDiffMinMax;
                if (t < 0f) t = 0f;
                if (t > 1f) t = 1f;
                return effectivenessAtMin + (effectivenessAtMax - effectivenessAtMin) * t; // MathHelper.Lerp
            }
            return 1.0;
        }

        // ---------------------------------------------------------------------------------------------- builders

        private static G2E3Row Row(string check, string value, string expected, bool pass, string note)
        {
            return new G2E3Row { Check = check, Value = value, Expected = expected, Status = pass ? G2E3Status.Pass : G2E3Status.Fail, Note = note };
        }

        private static G2E3Row Missing(string check, string expected, string why = null)
        {
            return new G2E3Row { Check = check, Value = "unreadable", Expected = expected, Status = G2E3Status.Fail, Note = why };
        }

        private static G2E3Row Near(string check, double? v, double expected, double tol, string note)
        {
            string exp = G(expected) + "+-" + G(tol);
            if (!v.HasValue || double.IsNaN(v.Value) || double.IsInfinity(v.Value)) return Missing(check, exp, note);
            return Row(check, G(v.Value), exp, Math.Abs(v.Value - expected) <= tol, note);
        }

        private static G2E3Row AtLeast(string check, double? v, double min, string note)
        {
            string exp = ">=" + G(min);
            if (!v.HasValue || double.IsNaN(v.Value) || double.IsInfinity(v.Value)) return Missing(check, exp, note);
            return Row(check, G(v.Value), exp, v.Value >= min, note);
        }

        private static G2E3Row Bits(string check, uint? bits, uint expected, string note)
        {
            string exp = Hex(expected) + "(" + G(SiteValidation.FloatFromBits(expected)) + ")";
            if (!bits.HasValue) return Missing(check, exp, note);
            return Row(check, Hex(bits.Value) + "(" + G(SiteValidation.FloatFromBits(bits.Value)) + ")", exp, bits.Value == expected, note);
        }

        private static G2E3Row ZeroF(string check, float? v, string why)
        {
            if (!v.HasValue) return Missing(check, "0", why);
            return Row(check, G(v.Value), "0", v.Value == 0f, null);
        }

        private static G2E3Row FalseB(string check, bool? v, string why)
        {
            if (!v.HasValue) return Missing(check, "False", why);
            return Row(check, v.Value ? "True" : "False", "False", !v.Value, null);
        }

        private static string Join(string a, string b)
        {
            return string.IsNullOrEmpty(b) ? a : a + "; " + b;
        }

        private static string Hex(uint v)
        {
            return "0x" + v.ToString("x8", CultureInfo.InvariantCulture);
        }

        /// <summary>Round-trip number without spaces (G17 for doubles; float values pass through double exactly).</summary>
        public static string G(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "NaN";
            string r = v.ToString("R", CultureInfo.InvariantCulture);
            return r;
        }

        /// <summary>"(x,y,z)" without spaces.</summary>
        public static string P(Vec3 v)
        {
            return "(" + G(v.X) + "," + G(v.Y) + "," + G(v.Z) + ")";
        }
    }
}
