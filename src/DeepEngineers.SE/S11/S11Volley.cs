using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;
using Sandbox.Definitions;
using Sandbox.Game.Weapons;
using VRage.Game;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// R rows, SE's own
 /// missiles fired at a point Subnautica names (a pinned creature's centre), dev only:
 /// <c>de.s11 {volley: {sn: [x, y, z], count, kind: small | large | rail | smallrail, from: m, every: s, origin: "gridId"}}</c>.
 /// Each missile is SE's: <c>MyMissile.PrepareBuilder(MyWeaponPropertiesWrapper, Vector3D, Vector3, Vector3, long, long, long)</c>
 /// and <c>MyMissiles.Static.Add</c>, the launchers' own path
 /// the weapon and magazine ids are the game's
 /// (Weapons.sbc: SmallMissileLauncher / LargeMissileLauncher with Missile200mm, SmallRailgun / LargeRailgun with
 /// SmallRailgunAmmo / LargeRailgunAmmo); <c>new MyWeaponPropertiesWrapper(MyDefinitionId)</c>,
 /// <c>ChangeAmmoMagazine(MyDefinitionId)</c>, <c>GetCurrentAmmoDefinitionAs&lt;T&gt;</c>,
 /// <c>MyMissileAmmoDefinition.MissileInitialSpeed</c>, <c>MyDefinitionId.TryParse(string, out MyDefinitionId)</c>
 /// The origin entity sets the weapon class of the explosion (a small grid: SmallGrid). Rows
 /// <c>s11.volley</c> (one per missile, its entity id). Game thread (S11Consumer.Tick).
 /// </summary>
    internal static class S11Volley
    {
        private sealed class Shot
        {
            public int Index, Count;
            public string Kind;
            public Vector3D Target;
            public double From;
            public long Origin;
            public ulong DueMs;
        }

        private static readonly Queue<Shot> Pending = new Queue<Shot>();
        public static long Fired, Failed;
        public static string LastError = "";

        public static string Describe() { return "fired " + Fired + ", failed " + Failed + ", pending " + Pending.Count + (LastError.Length > 0 ? ", last error " + LastError : ""); }

        public static void Clear() { Pending.Clear(); }

        /// <summary>Queues <paramref name="count"/> missiles at the SN point (mapped to SE), one every <paramref name="everyS"/>.</summary>
        public static Dictionary<string, object> Queue(IDictionary<string, object> a, ulong nowMs)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            SiteMap map = SiteBinding.Map;
            object v;
            var l = a != null && a.TryGetValue("sn", out v) ? v as System.Collections.IList : null;
            if (map == null || l == null || l.Count != 3) { d["ok"] = false; d["error"] = "volley: {sn: [x, y, z], count, kind: small|large|rail|smallrail, from, every, origin} (needs the site map)"; return d; }
            Vec3 se = map.SnToSe(new Vec3(Convert.ToDouble(l[0], CultureInfo.InvariantCulture), Convert.ToDouble(l[1], CultureInfo.InvariantCulture), Convert.ToDouble(l[2], CultureInfo.InvariantCulture)));
            int count = Math.Max(1, Math.Min(50, (int)Num(a, "count", 10)));
            string kind = a.TryGetValue("kind", out v) ? Convert.ToString(v, CultureInfo.InvariantCulture) : "small";
            double from = Math.Max(10, Math.Min(400, Num(a, "from", 80)));
            double every = Math.Max(0.05, Math.Min(10, Num(a, "every", 0.4)));
            long origin = 0;
            if (a.TryGetValue("origin", out v) && v != null) long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out origin);
            for (int i = 0; i < count; i++)
                Pending.Enqueue(new Shot { Index = i, Count = count, Kind = kind, Target = new Vector3D(se.X, se.Y, se.Z), From = from, Origin = origin, DueMs = nowMs + (ulong)(i * every * 1000.0) });
            DeLog.Info("DE.S11 volley: " + count + " " + kind + " missile(s) queued at SE " + S11SeText.G(se.X) + "," + S11SeText.G(se.Y) + "," + S11SeText.G(se.Z) + " from " + S11SeText.G(from) + " m, every " + S11SeText.G(every) + " s, origin " + origin);
            d["ok"] = true;
            d["queued"] = count;
            d["target"] = new object[] { se.X, se.Y, se.Z };
            return d;
        }

        public static void Tick(ulong nowMs)
        {
            int n = 0;
            while (Pending.Count > 0 && Pending.Peek().DueMs <= nowMs && n++ < 4) Fire(Pending.Dequeue());
        }

        private static void Ids(string kind, out string weapon, out string magazine)
        {
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "large": weapon = "LargeMissileLauncher"; magazine = "Missile200mm"; return;
                case "rail": weapon = "LargeRailgun"; magazine = "LargeRailgunAmmo"; return;
                case "smallrail": weapon = "SmallRailgun"; magazine = "SmallRailgunAmmo"; return;
                default: weapon = "SmallMissileLauncher"; magazine = "Missile200mm"; return;
            }
        }

        private static void Fire(Shot s)
        {
            try
            {
                MyMissiles ms = MyMissiles.Static;
                var cam = Sandbox.Game.World.MySector.MainCamera;
                if (ms == null) { Failed++; LastError = "no MyMissiles"; return; }
                Vector3D toCam = cam != null ? cam.Position - s.Target : Vector3D.Up;
                if (toCam.LengthSquared() < 1e-6) toCam = Vector3D.Up;
                toCam.Normalize();
                // in-game 20261024-122326: fired along the camera line, the missiles started behind the harness character and met it
 // first (Subnautica: 4 "blocked", 9 never seen); now from the side, square to the camera line and level with the
 // target (the planet's up: against MyGravityProviderSystem.CalculateNaturalGravityInPoint), spread per shot
                Vector3 gv = Sandbox.Game.GameSystems.MyGravityProviderSystem.CalculateNaturalGravityInPoint(s.Target);
                Vector3D up = gv.LengthSquared() > 1e-6f ? -Vector3D.Normalize((Vector3D)gv) : Vector3D.Up;
                Vector3D side = Vector3D.Cross(toCam, up);
                if (side.LengthSquared() < 1e-6) side = Vector3D.CalculatePerpendicularVector(up);
                side.Normalize();
                double ang = s.Index * 2.399963;
                Vector3D start = s.Target + side * s.From + up * (Math.Sin(ang) * 2.0) + toCam * (Math.Cos(ang) * 2.0);
                Vector3 dir = Vector3.Normalize((Vector3)(s.Target - start));
                string weapon, magazine;
                Ids(s.Kind, out weapon, out magazine);
                MyDefinitionId wid, mid;
                if (!MyDefinitionId.TryParse("MyObjectBuilder_WeaponDefinition/" + weapon, out wid) || !MyDefinitionId.TryParse("MyObjectBuilder_AmmoMagazine/" + magazine, out mid))
                { Failed++; LastError = "definition ids " + weapon + "/" + magazine; return; }
                var props = new MyWeaponPropertiesWrapper(wid);
                props.ChangeAmmoMagazine(mid);
                MyMissileAmmoDefinition ammo = props.GetCurrentAmmoDefinitionAs<MyMissileAmmoDefinition>();
                float v0 = ammo != null ? Math.Max(1f, ammo.MissileInitialSpeed) : 100f;
                // through reflection: MyObjectBuilder_Missile lives in SpaceEngineers.ObjectBuilders, which this plugin does not reference
                if (!Reflect()) { Failed++; LastError = "MyMissile.PrepareBuilder / MyMissiles.Add not found"; return; }
                object b = _prepare.Invoke(null, new object[] { props, start, dir * v0, dir, 0L, s.Origin, s.Origin });
                var eb = b as VRage.ObjectBuilders.MyObjectBuilder_EntityBase;
                long id = eb != null ? eb.EntityId : 0L;
                _add.Invoke(ms, new object[] { b });
                Fired++;
                var sb = new System.Text.StringBuilder(256);
                sb.Append("{\"t\":"); JsonText.AppendString(sb, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                sb.Append(",\"ev\":\"s11.volley\"");
                S5Rows.Int(sb, "i", s.Index);
                S5Rows.Int(sb, "count", s.Count);
                S5Rows.Str(sb, "kind", s.Kind);
                S5Rows.Id(sb, "missile", id);
                S5Rows.Num(sb, "from", s.From);
                S5Rows.Id(sb, "origin", s.Origin);
                S5Rows.Num(sb, "speed", v0);
                Jsonl.WriteLineBuffered(S5Rows.End(sb));
            }
            catch (Exception e)
            {
                Failed++;
                LastError = e.GetType().Name + ": " + e.Message;
                DeLog.Info("DE.S11 volley shot failed: " + LastError);
            }
        }

        private static System.Reflection.MethodInfo _prepare, _add;
        private static bool _reflected;

        private static bool Reflect()
        {
            if (_reflected) return _prepare != null && _add != null;
            _reflected = true;
            foreach (System.Reflection.MethodInfo m in typeof(MyMissile).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                if (m.Name == "PrepareBuilder" && m.GetParameters().Length == 7 && m.GetParameters()[0].ParameterType == typeof(MyWeaponPropertiesWrapper)) _prepare = m;
            foreach (System.Reflection.MethodInfo m in typeof(MyMissiles).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                if (m.Name == "Add" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.Name == "MyObjectBuilder_Missile") _add = m;
            if (_prepare == null || _add == null) DeLog.Info("DE.S11 MISSING MyMissile.PrepareBuilder(MyWeaponPropertiesWrapper, Vector3D, Vector3, Vector3, Int64, Int64, Int64) / MyMissiles.Add(MyObjectBuilder_Missile) (no scripted volley)");
            return _prepare != null && _add != null;
        }

        private static double Num(IDictionary<string, object> a, string k, double dflt)
        {
            object v;
            if (a == null || !a.TryGetValue(k, out v) || v == null) return dflt;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch (Exception) { return dflt; }
        }
    }
}
