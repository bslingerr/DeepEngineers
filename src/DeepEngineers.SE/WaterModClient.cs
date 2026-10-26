using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage;
using VRage.Game.Components;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// Our own client of the Water Mod's message API (CONTRACT §0: "our own code written from the message-handler
 /// behaviour (handler id, delegate dictionary keys and types, VerifyVersion), never a copy of the mod's
 /// WaterModAPI.cs"). Behaviour read in the Water Mod 3.23 sources:
 /// <list type="bullet">
 /// <item>the mod's <c>WaterAPIComponent.BeforeStart</c> calls <c>MyAPIGateway.Utilities.SendModMessage(50271,
 /// Dictionary&lt;string, Delegate&gt)</c> once per session; SE's <c>MyAPIUtilities.SendModMessage</c> calls the
 /// handlers registered at that moment and buffers nothing, so the
 /// handler is registered in our session component's <c>LoadData</c>
 /// </item>
 /// <item><c>MyAPIUtilities.Static</c> is process-wide, so the handler is unregistered in <c>UnloadData</c>;</item>
 /// <item>the backend's <c>VerifyVersion(int, string)</c> returns false only below its minimum (19);</item>
 /// <item>keys and delegate types used here: <c>VerifyVersion</c> Func&lt;int, string, bool&gt;, <c>HasWater</c>
 /// Func&lt;MyPlanet, bool&gt;, <c>GetPhysicalData</c> Func&lt;MyPlanet, MyTuple&lt;Vector3D, float, float, float&gt;&gt;
 /// (centre, radius, min, max), <c>GetDepth</c> Func&lt;Vector3D, MyPlanet, float?&gt; (negative below the surface),
 /// <c>GetWaveData</c> Func&lt;MyPlanet, MyTuple&lt;float, float, float, int&gt;&gt; (height, speed, scale, seed),
 /// <c>GetTideData</c> Func&lt;MyPlanet, MyTuple&lt;float, float&gt;&gt; (height, speed), <c>GetClosestWater</c>
 /// Func&lt;Vector3D, MyPlanet&gt;, <c>SetWaterRadius</c> Func&lt;MyPlanet, float, bool&gt; (server only; multiplier of
 /// MinimumRadius, clamped 0.1…1.75);</item>
 /// <item>crush damage and current speed have no API getter: they are read by reflection from the planet's
 /// <c>Jakaria.Components.WaterComponent.Settings</c> (<c>Jakaria.WaterSettings</c> float fields <c>CrushDamage</c>,
 /// <c>CurrentSpeed</c>, <c>WaveHeight</c>, <c>TideHeight</c>, bool <c>Volumetric</c>; optional bool <c>EnableFish</c> for
 /// <c>de.g2e3</c>), only when
 /// <c>Jakaria.WaterData.Version</c> (a string const, read by reflection) is the pinned "3.23".</item>
 /// </list>
 /// Update thread only. Never throws into the caller: the Water Mod invokes the handler from its own BeforeStart,
 /// where an exception would break its session start.
 /// </summary>
    internal sealed class WaterModClient
    {
        public const long HandlerId = 50271;
        public const int ApiVersion = 22;
        public const string ModName = "DeepEngineers";
        /// <summary>CONTRACT §0 pin: Water Mod 3.23 (<c>Jakaria.WaterData.Version</c>).</summary>
        public const string PinnedVersion = "3.23";

        private readonly Action<object> _handler;
        private bool _registered;

        private Func<int, string, bool> _verifyVersion;
        private Func<MyPlanet, bool> _hasWater;
        private Func<MyPlanet, MyTuple<Vector3D, float, float, float>> _getPhysicalData;
        private Func<Vector3D, MyPlanet, float?> _getDepth;
        private Func<MyPlanet, MyTuple<float, float, float, int>> _getWaveData;
        private Func<MyPlanet, MyTuple<float, float>> _getTideData;
        private Func<Vector3D, MyPlanet> _getClosestWater;
        private Func<MyPlanet, float, bool> _setWaterRadius;

        // version-gated reflection on the mod's in-memory script assembly
        private Type _waterComponentType;
        private FieldInfo _fSettings, _fRadius, _fCrush, _fCurrent, _fWave, _fTide, _fVolumetric, _fSettingsRadius;
        // optional (de.g2e3 only; its absence never affects the site validation)
        private FieldInfo _fFish;
        private string _reflectionError;

        // S7p-gameplay (P12): the grid buoyancy of the Water Mod 3.23 (Jakaria.Components.WaterPhysicsComponentGrid, an entity
 // component added to every grid; public fields of WaterPhysicsComponentBase: BuoyancyForce Vector3 (N, world),
 // FluidDepth double, PercentUnderwater float, SimulatePhysics bool), read through compiled getters (no boxing; the
 // reads run in SE's parallel thrust update: immutable after PrepareReflection)
        private volatile GridBuoyancyReader _gridBuoyancy;
        public string GridBuoyancyError { get; private set; }

        public WaterModClient()
        {
            _handler = OnMessage;
        }

        /// <summary>The dictionary arrived (on handler 50271).</summary>
        public bool Received { get; private set; }
        public int Messages { get; private set; }
        public int KeyCount { get; private set; }
        /// <summary>VerifyVersion false, a missing key or a delegate of another type; null when the API is usable.</summary>
        public string Error { get; private set; }
        public bool? VerifyResult { get; private set; }
        /// <summary><c>Jakaria.WaterData.Version</c> read by reflection, or null.</summary>
        public string WaterModVersion { get; private set; }
        public bool VersionPinned { get { return WaterModVersion == PinnedVersion; } }
        /// <summary>Where the registration happened ("LoadData", or the first UpdateBeforeSimulation under S1.M9).</summary>
        public string RegisteredIn { get; private set; }
        /// <summary>The handler is registered now.</summary>
        public bool Registered { get { return _registered; } }
        public string ModAssembly { get; private set; }
        public bool Usable { get { return Received && Error == null; } }

        // ================================================================================================ handler

        public void Register(string where)
        {
            if (_registered) return;
            MyAPIUtilities.Static.RegisterMessageHandler(HandlerId, _handler);
            _registered = true;
            RegisteredIn = where;
        }

        public void Unregister()
        {
            if (!_registered) return;
            try { MyAPIUtilities.Static.UnregisterMessageHandler(HandlerId, _handler); } catch (Exception) { }
            _registered = false;
        }

        private void OnMessage(object payload)
        {
            try { OnMessageCore(payload); }
            catch (Exception e)
            {
                Error = "handler error " + e.GetType().Name + ": " + e.Message;
                DeLog.Info("DE.WaterApi ERROR " + Error);
            }
        }

        private void OnMessageCore(object payload)
        {
            Messages++;
            var dict = payload as Dictionary<string, Delegate>;
            if (dict == null)
            {
                DeLog.Info("DE.WaterApi ignored a message on " + HandlerId + " of type " + (payload == null ? "null" : payload.GetType().FullName));
                return;
            }
            if (Received)
            {
                DeLog.Info("DE.WaterApi note: a second delegate dictionary arrived (message " + Messages + "); the first is kept");
                return;
            }
            Received = true;
            KeyCount = dict.Count;

            var missing = new List<string>();
            _verifyVersion = Take<Func<int, string, bool>>(dict, "VerifyVersion", missing);
            _hasWater = Take<Func<MyPlanet, bool>>(dict, "HasWater", missing);
            _getPhysicalData = Take<Func<MyPlanet, MyTuple<Vector3D, float, float, float>>>(dict, "GetPhysicalData", missing);
            _getDepth = Take<Func<Vector3D, MyPlanet, float?>>(dict, "GetDepth", missing);
            _getWaveData = Take<Func<MyPlanet, MyTuple<float, float, float, int>>>(dict, "GetWaveData", missing);
            _getTideData = Take<Func<MyPlanet, MyTuple<float, float>>>(dict, "GetTideData", missing);
            _getClosestWater = Take<Func<Vector3D, MyPlanet>>(dict, "GetClosestWater", missing);
            _setWaterRadius = Take<Func<MyPlanet, float, bool>>(dict, "SetWaterRadius", missing);

            // WaterData.Version by reflection, from the assembly that holds the delegates' code (the mod's scripts)
            Assembly modAsm = null;
            foreach (KeyValuePair<string, Delegate> kv in dict)
            {
                if (kv.Value != null && kv.Value.Method != null && kv.Value.Method.DeclaringType != null)
                {
                    modAsm = kv.Value.Method.DeclaringType.Assembly;
                    break;
                }
            }
            ModAssembly = modAsm != null ? modAsm.GetName().Name : "unknown";
            WaterModVersion = ReadWaterDataVersion(modAsm);
            if (VersionPinned) PrepareReflection(modAsm);
            else _reflectionError = "Jakaria.WaterData.Version=" + (WaterModVersion ?? "unreadable") + ", reflection pinned to " + PinnedVersion;

            if (missing.Count > 0)
                Error = "delegate check failed: " + string.Join(", ", missing.ToArray());
            else
            {
                bool ok = _verifyVersion(ApiVersion, ModName);
                VerifyResult = ok;
                if (!ok) Error = "VerifyVersion(" + ApiVersion + ", \"" + ModName + "\") returned false";
            }

            DeLog.Info("DE.WaterApi RECEIVED on " + HandlerId + " (registered in " + RegisteredIn + ") keys=" + KeyCount +
                       " VerifyVersion(" + ApiVersion + ",\"" + ModName + "\")=" + (VerifyResult.HasValue ? VerifyResult.Value.ToString() : "not called") +
                       " WaterData.Version=" + (WaterModVersion ?? "unreadable") +
                       (VersionPinned ? " (pinned)" : " (WARNING: pinned " + PinnedVersion + ")") + " assembly=" + ModAssembly +
                       (Error != null ? " ERROR " + Error : " delegates OK: VerifyVersion, HasWater, GetPhysicalData, GetDepth, GetWaveData, GetTideData, GetClosestWater, SetWaterRadius") +
                       (_reflectionError != null ? "; settings reflection off: " + _reflectionError : "; settings reflection ready"));
        }

        /// <summary>Takes a key whose delegate must be exactly <typeparamref name="T"/>; records "key missing" / "key type X".</summary>
        private static T Take<T>(Dictionary<string, Delegate> dict, string key, List<string> problems) where T : class
        {
            Delegate d;
            if (!dict.TryGetValue(key, out d) || d == null)
            {
                problems.Add(key + " missing");
                return null;
            }
            if (d.GetType() != typeof(T))
            {
                problems.Add(key + " type " + TypeName(d.GetType()) + " (expected " + TypeName(typeof(T)) + ")");
                return null;
            }
            return d as T;
        }

        /// <summary>"Func&lt;MyPlanet, Boolean&gt;" rather than "Func`2".</summary>
        private static string TypeName(Type t)
        {
            if (!t.IsGenericType) return t.Name;
            string n = t.Name;
            int tick = n.IndexOf('`');
            if (tick > 0) n = n.Substring(0, tick);
            Type[] args = t.GetGenericArguments();
            var parts = new string[args.Length];
            for (int i = 0; i < args.Length; i++) parts[i] = TypeName(args[i]);
            return n + "<" + string.Join(", ", parts) + ">";
        }

        private static string ReadWaterDataVersion(Assembly asm)
        {
            if (asm == null) return null;
            try
            {
                Type t = asm.GetType("Jakaria.WaterData", false);
                if (t == null) return null;
                FieldInfo f = t.GetField("Version", BindingFlags.Public | BindingFlags.Static);
                if (f == null || f.FieldType != typeof(string)) return null;
                return f.GetValue(null) as string;
            }
            catch (Exception) { return null; }
        }

        private void PrepareReflection(Assembly asm)
        {
            try
            {
                _waterComponentType = asm.GetType("Jakaria.Components.WaterComponent", false);
                if (_waterComponentType == null) { _reflectionError = "type Jakaria.Components.WaterComponent not found"; return; }
                _fSettings = _waterComponentType.GetField("Settings", BindingFlags.Public | BindingFlags.Instance);
                _fRadius = _waterComponentType.GetField("Radius", BindingFlags.Public | BindingFlags.Instance);
                if (_fSettings == null || _fRadius == null || _fRadius.FieldType != typeof(double))
                {
                    _reflectionError = "WaterComponent.Settings/Radius not found";
                    return;
                }
                Type st = _fSettings.FieldType;
                _fCrush = Field(st, "CrushDamage", typeof(float));
                _fCurrent = Field(st, "CurrentSpeed", typeof(float));
                _fWave = Field(st, "WaveHeight", typeof(float));
                _fTide = Field(st, "TideHeight", typeof(float));
                _fVolumetric = Field(st, "Volumetric", typeof(bool));
                _fSettingsRadius = Field(st, "Radius", typeof(float));
                _fFish = Field(st, "EnableFish", typeof(bool)); // Water Mod 3.23 WaterSettings.EnableFish (public bool field)
                PrepareGridBuoyancy(asm); // S7p-gameplay (P12): optional, its absence turns only the depth hold off
                if (_fCrush == null || _fCurrent == null || _fWave == null || _fTide == null || _fVolumetric == null || _fSettingsRadius == null)
                {
                    _reflectionError = st.FullName + ": CrushDamage/CurrentSpeed/WaveHeight/TideHeight/Volumetric/Radius not all found with their types";
                    return;
                }
                _reflectionError = null;
            }
            catch (Exception e)
            {
                _reflectionError = "reflection error " + e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>S7p-gameplay (P12): the grid physics component type and its four fields; a miss turns only the depth hold off.</summary>
        private void PrepareGridBuoyancy(Assembly asm)
        {
            try
            {
                Type t = asm.GetType("Jakaria.Components.WaterPhysicsComponentGrid", false);
                if (t == null) { GridBuoyancyError = "type Jakaria.Components.WaterPhysicsComponentGrid not found"; return; }
                FieldInfo force = Field(t, "BuoyancyForce", typeof(Vector3));
                FieldInfo depth = Field(t, "FluidDepth", typeof(double));
                FieldInfo pct = Field(t, "PercentUnderwater", typeof(float));
                FieldInfo sim = Field(t, "SimulatePhysics", typeof(bool));

                FieldInfo centre = Field(t, "CenterOfBuoyancy", typeof(Vector3D));
                if (force == null || depth == null || pct == null || sim == null)
                {
                    GridBuoyancyError = t.FullName + ": BuoyancyForce/FluidDepth/PercentUnderwater/SimulatePhysics not all found with their types";
                    return;
                }
                _gridBuoyancy = new GridBuoyancyReader(t, Getter<Vector3>(t, force), Getter<double>(t, depth), Getter<float>(t, pct), Getter<bool>(t, sim),
                                                       centre != null ? Getter<Vector3D>(t, centre) : null);
                GridBuoyancyError = null;
            }
            catch (Exception e)
            {
                GridBuoyancyError = "grid buoyancy reflection error " + e.GetType().Name + ": " + e.Message;
            }
        }

        private static Func<object, T> Getter<T>(Type owner, FieldInfo f)
        {
            ParameterExpression o = Expression.Parameter(typeof(object), "o");
            return Expression.Lambda<Func<object, T>>(Expression.Field(Expression.Convert(o, owner), f), o).Compile();
        }

        /// <summary>The compiled field getters of the Water Mod's grid physics component (immutable; thread-safe).</summary>
        internal sealed class GridBuoyancyReader
        {
            public readonly Type ComponentType;
            public readonly Func<object, Vector3> Force;
            public readonly Func<object, double> Depth;
            public readonly Func<object, float> Percent;
            public readonly Func<object, bool> Simulate;
            /// <summary>The centre of buoyancy (world) the mod applies its force at; null when the field is not there (no torque hold).</summary>
            public readonly Func<object, Vector3D> Centre;

            public GridBuoyancyReader(Type t, Func<object, Vector3> force, Func<object, double> depth, Func<object, float> pct, Func<object, bool> sim,
                                      Func<object, Vector3D> centre)
            {
                ComponentType = t; Force = force; Depth = depth; Percent = pct; Simulate = sim; Centre = centre;
            }
        }

        /// <summary>
 /// S7p-gameplay (P12): the Water Mod's buoyancy on a grid and whether the mod applies it now
 /// (<see cref="DampenerHoldRules.BuoyancyApplied"/>). Thread-safe (SE's parallel thrust update); false when the
 /// API or the reflection is not ready, or the grid has no water physics component.
 /// </summary>
        public bool TryReadGridBuoyancy(MyCubeGrid grid, out Vector3 force, out bool applied)
        {
            force = Vector3.Zero;
            applied = false;
            GridBuoyancyReader r = _gridBuoyancy;
            if (r == null || grid == null) return false;
            try
            {
                MyComponentBase c;
                if (!grid.Components.TryGet(r.ComponentType, out c) || c == null) return false;
                force = r.Force(c);
                double radius = grid.PositionComp.WorldVolume.Radius;
                applied = DampenerHoldRules.BuoyancyApplied(r.Depth(c), radius, r.Percent(c), r.Simulate(c));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool GridBuoyancyReady { get { return _gridBuoyancy != null; } }
        public bool GridBuoyancyCentreReady { get { return _gridBuoyancy != null && _gridBuoyancy.Centre != null; } }

        /// <summary>The buoyancy and the world point it acts at (P12 torque hold); false when the centre field is not readable.</summary>
        public bool TryReadGridBuoyancy(MyCubeGrid grid, out Vector3 force, out bool applied, out Vector3D centre)
        {
            centre = Vector3D.Zero;
            if (!TryReadGridBuoyancy(grid, out force, out applied)) return false;
            GridBuoyancyReader r = _gridBuoyancy;
            if (r == null || r.Centre == null) return false;
            try
            {
                MyComponentBase c;
                if (!grid.Components.TryGet(r.ComponentType, out c) || c == null) return false;
                centre = r.Centre(c);
                return true;
            }
            catch (Exception) { return false; }
        }

        private static FieldInfo Field(Type t, string name, Type type)
        {
            FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return f != null && f.FieldType == type ? f : null;
        }

        // ================================================================================================ queries

        public bool? HasWater(MyPlanet planet)
        {
            if (_hasWater == null || planet == null) return null;
            try { return _hasWater(planet); } catch (Exception) { return null; }
        }

        /// <summary>Sea radius bits (<c>GetPhysicalData(planet).Item2</c>), S1.M5 applied; null when unreadable.</summary>
        public uint? RsBits(MyPlanet planet)
        {
            if (_getPhysicalData == null || planet == null) return null;
            try
            {
                if (_hasWater != null && !_hasWater(planet)) return null; // the backend reads the component without a null check
                float rs = _getPhysicalData(planet).Item2;
                return SiteValidation.ApplyRsMutant(Wire.SiteV1.FloatBits(rs));
            }
            catch (Exception) { return null; }
        }

        /// <summary><c>GetDepth(P, planet)</c> (negative below the surface), or null.</summary>
        public float? Depth(Vector3D p, MyPlanet planet)
        {
            if (_getDepth == null || planet == null) return null;
            try { return _getDepth(p, planet); } catch (Exception) { return null; }
        }

        /// <summary>Wave height from <c>GetWaveData</c>, or null.</summary>
        public float? WaveHeight(MyPlanet planet)
        {
            if (_getWaveData == null || planet == null) return null;
            try { return _getWaveData(planet).Item1; } catch (Exception) { return null; }
        }

        /// <summary>Tide height from <c>GetTideData</c>, or null.</summary>
        public float? TideHeight(MyPlanet planet)
        {
            if (_getTideData == null || planet == null) return null;
            try { return _getTideData(planet).Item1; } catch (Exception) { return null; }
        }

        public MyPlanet ClosestWater(Vector3D p)
        {
            if (_getClosestWater == null) return null;
            try { return _getClosestWater(p); } catch (Exception) { return null; }
        }

        /// <summary>Server only (Water Mod: <c>SetWaterRadius</c> returns false on a client): the sea radius multiplier.</summary>
        public bool SetWaterRadius(MyPlanet planet, float multiplier, out string why)
        {
            why = null;
            if (_setWaterRadius == null) { why = "SetWaterRadius unavailable"; return false; }
            try { return _setWaterRadius(planet, multiplier); }
            catch (Exception e) { why = e.GetType().Name + ": " + e.Message; return false; }
        }

        /// <summary>The per-planet settings the API does not expose, read by version-gated reflection.</summary>
        public bool TryReadSettings(MyPlanet planet, out WaterSettingsSnapshot s, out string why)
        {
            s = default(WaterSettingsSnapshot);
            if (_reflectionError != null || _waterComponentType == null) { why = _reflectionError ?? "API not received"; return false; }
            if (planet == null) { why = "no planet"; return false; }
            try
            {
                MyComponentBase comp;
                if (!planet.Components.TryGet(_waterComponentType, out comp) || comp == null)
                {
                    why = "no WaterComponent on the planet";
                    return false;
                }
                object settings = _fSettings.GetValue(comp);
                if (settings == null) { why = "WaterComponent.Settings is null"; return false; }
                s.CrushDamage = (float)_fCrush.GetValue(settings);
                s.CurrentSpeed = (float)_fCurrent.GetValue(settings);
                s.WaveHeight = (float)_fWave.GetValue(settings);
                s.TideHeight = (float)_fTide.GetValue(settings);
                s.Volumetric = (bool)_fVolumetric.GetValue(settings);
                s.SettingsRadius = (float)_fSettingsRadius.GetValue(settings);
                s.EnableFish = _fFish != null ? (bool?)(bool)_fFish.GetValue(settings) : null;
                s.ComponentRadius = (double)_fRadius.GetValue(comp);
                why = null;
                return true;
            }
            catch (Exception e)
            {
                why = "reflection read error " + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        /// <summary>The bound planet's WaterComponent and its Settings object for <see cref="WaterLook"/> (reflection, 3.23 names).</summary>
        internal bool TryGetWaterComponent(MyPlanet planet, out object comp, out object settings, out string why)
        {
            comp = settings = null;
            if (_reflectionError != null || _waterComponentType == null || _fSettings == null) { why = _reflectionError ?? "API not received"; return false; }
            if (planet == null) { why = "no planet (site not bound)"; return false; }
            try
            {
                MyComponentBase c;
                if (!planet.Components.TryGet(_waterComponentType, out c) || c == null) { why = "no WaterComponent on the planet"; return false; }
                comp = c;
                settings = _fSettings.GetValue(c);
                why = settings == null ? "WaterComponent.Settings is null" : null;
                return settings != null;
            }
            catch (Exception e) { why = "reflection error " + e.GetType().Name + ": " + e.Message; return false; }
        }

        public string Describe()
        {
            return "received=" + Received + " messages=" + Messages + " keys=" + KeyCount + " registeredIn=" + (RegisteredIn ?? "none") +
                   " verify=" + (VerifyResult.HasValue ? VerifyResult.Value.ToString() : "n/a") + " version=" + (WaterModVersion ?? "unknown") +
                   " pinned=" + VersionPinned + " error=" + (Error ?? "none") + " reflection=" + (_reflectionError ?? "ready");
        }
    }

    /// <summary>Per-planet Water Mod settings read by reflection (Water Mod 3.23 field names).</summary>
    internal struct WaterSettingsSnapshot
    {
        public float CrushDamage;
        public float CurrentSpeed;
        public float WaveHeight;
        public float TideHeight;
        public bool Volumetric;
        /// <summary><c>WaterSettings.Radius</c>: the multiplier of MinimumRadius (site v1: 1.0526316f, bits 0x3F86BCA2).</summary>
        public float SettingsRadius;
        /// <summary><c>WaterComponent.Radius</c> (double) = MinimumRadius × Settings.Radius, what GetDepth uses.</summary>
        public double ComponentRadius;
        /// <summary><c>WaterSettings.EnableFish</c> (S1.G2-E3 check 3), null when the field was not found.</summary>
        public bool? EnableFish;
    }
}
