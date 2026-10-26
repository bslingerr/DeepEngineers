using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DeepEngineers.SE.Terrain;
using DeepEngineers.Wire;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>

 /// games"): on the first arrival of the local player in DE_4546B's gravity
 /// the plugin adds the GPS <see cref="GpsName"/> at the crash site
 /// and shows a short radio text once (chat line from <see cref="Sender"/> and a HUD notification).
 /// anywhere in the SE universe on the world's first session (space, another planet: the text then says the signal is faint and
 /// relayed), the planet's gravity a second chance when the GPS is missing on arrival; each stage once per world (the marker
 /// file <c>beacon.done</c> in the world's store folder records them; a GPS of that name already in the list is never doubled).
 /// The text is a placeholder (S15 writes the lore); nothing of either game's text is copied. <c>de.s8a beacon</c> (dev
 /// flavour) re-triggers: removes the GPS and the marker. Env <c>DEEPENGINEERS_BEACON=off</c> turns it off. Row
 /// <c>s8a.beacon</c> {tick, action added|retrigger|skipped, identity, se, sn, text}.
 /// </summary>
    internal static class S8aBeacon
    {
        public const string EnvBeacon = "DEEPENGINEERS_BEACON";
        public const string GpsName = "Aurora distress signal";
        public const string Sender = "Alterra automated beacon";
        public const string MarkerFile = "beacon.done";
        /// <summary>
 /// The crash site in Subnautica's coordinates: the Aurora's root (SN (764.4, -70.2, -138.6): the root transform of the
 /// install's aurora scene bundle, read read-only with UnityPy on), at the waterline.
 /// </summary>
        public static readonly Vec3 CrashSiteSn = new Vec3(764.4, 0.0, -138.6);
        public const ulong CheckEveryMs = 1000;

        private static ulong _nextMs;
        private static bool _done;
        private static string _worldPath;
        public static int Added, Shown, Retriggers;
        public static string LastText;

        public static bool On { get { return S8aOverlay.EnvOn(EnvBeacon, true); } }

        /// <summary>The placeholder text when the player is outside DE_4546B's gravity (S15 replaces it).</summary>
        public static string FaintText(double km)
        {
            return "[placeholder] ...faint signal, relayed... Alterra vessel Aurora... down on planet 4546B... survivors unknown... (" +
                   km.ToString("0", CultureInfo.InvariantCulture) + " km; position attached: GPS '" + GpsName + "')";
        }

        /// <summary>The placeholder radio text (S15 replaces it).</summary>
        public static string RadioText(Vec3 sn)
        {
            return "[placeholder] Automated distress call. Alterra vessel Aurora down on planet 4546B, position attached (GPS '" + GpsName +
                   "'). Survivors: unknown. Repeating.";
        }

        private static string MarkerPath(string worldPath)
        {
            ulong key = S8aStore.StoreKey(worldPath, SiteV1.SiteVersion);
            return Path.Combine(S8aStore.StoreFolder(S8aStore.DefaultRoot(), key), MarkerFile);
        }

        /// <summary>Per tick (update thread). Never throws.</summary>
        public static void Tick(ulong nowMs, bool inWorld)
        {
            if (!inWorld) { _done = false; _worldPath = null; return; }
            if (nowMs < _nextMs) return;
            _nextMs = nowMs + CheckEveryMs;
            try
            {
                MySession s = MySession.Static;
                if (s == null || !On) return;
                string path = s.CurrentPath;
                if (path != _worldPath) { _worldPath = path; _done = false; }
                if (_done) return;
                if (!SiteBinding.IsOk) return;   //: never before this world's own binding (a stale map put the GPS in space)
                MyPlanet planet = SiteBinding.Planet;
                SiteMap map = SiteBinding.SiteMapV1 ?? SiteBinding.Map;
                IMyGpsCollection gpss = MyAPIGateway.Session != null ? MyAPIGateway.Session.GPS : null;
                if (planet == null || map == null || gpss == null || s.LocalHumanPlayer == null || string.IsNullOrEmpty(path)) return;
                Vector3D pos = s.LocalHumanPlayer.GetPosition();
                MyGravityProviderComponent grav = planet.Components.Get<MyGravityProviderComponent>();
                bool inGravity = grav != null && grav.IsPositionInRange(pos);
                long identity = s.LocalHumanPlayer.Identity.IdentityId;
                string marker = MarkerPath(path);
                string stages = File.Exists(marker) ? File.ReadAllText(marker) : "";
                bool anywhere = stages.Contains(StageAnywhere), gravity = stages.Contains(StageGravity);

                // the first session wherever the player is; the planet's gravity is the second chance when that one was missed (the GPS
 // not in the list on arrival); at most these two, each once
                if (!anywhere)
                {
                    if (!HasGps(gpss, identity)) Add(gpss, map, identity, "added", inGravity ? "gravity" : "anywhere", !inGravity, pos);
                    MarkStage(marker, StageAnywhere + (inGravity ? "\n" + StageGravity : ""));
                    _done = inGravity;
                    return;
                }
                if (!inGravity) return;                       // the second chance waits for the planet's gravity
                if (!gravity)
                {
                    if (!HasGps(gpss, identity)) Add(gpss, map, identity, "added", "gravity", false, pos);
                    MarkStage(marker, StageGravity);
                }
                _done = true;
            }
            catch (Exception e) { DeLog.Info("DE.S8a beacon " + e.GetType().Name + ": " + e.Message); _done = true; }
        }

        public const string StageAnywhere = "stage anywhere", StageGravity = "stage gravity";

        private static void MarkStage(string marker, string stage)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(marker));
            File.AppendAllText(marker, (File.Exists(marker) ? "" : "de-s8a beacon 2\n") + stage + "\t" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\n");
        }

        private static bool HasGps(IMyGpsCollection gpss, long identity)
        {
            List<IMyGps> list = gpss.GetGpsList(identity);
            if (list == null) return false;
            foreach (IMyGps g in list) if (g != null && g.Name == GpsName) return true;
            return false;
        }

        private static void Add(IMyGpsCollection gpss, SiteMap map, long identity, string action, string where = "gravity", bool far = false, Vector3D player = default(Vector3D))
        {
            Vec3 se = map.SnToSe(CrashSiteSn);
            IMyGps gps = gpss.Create(GpsName, "Automated Alterra distress signal (placeholder text, S15)", new Vector3D(se.X, se.Y, se.Z), true, false);
            gpss.AddGps(identity, gps);
            Added++;
            double km = (new Vector3D(se.X, se.Y, se.Z) - player).Length() / 1000.0;
            string text = far ? FaintText(km) : RadioText(CrashSiteSn);
            LastText = text;
            try { MyAPIGateway.Utilities.ShowMessage(Sender, text); Shown++; } catch (Exception) { }
            try { MyAPIGateway.Utilities.ShowNotification("Distress signal received: " + GpsName, 8000, "Blue"); } catch (Exception) { }
            Jsonl.WriteLine(S4Rows.Row("s8a.beacon", "tick", (long)GameState.GameplayTick, "action", action, "where", where, "far", far, "km", Math.Round(km, 1), "identity", identity,
                                       "se", new object[] { se.X, se.Y, se.Z }, "sn", new object[] { CrashSiteSn.X, CrashSiteSn.Y, CrashSiteSn.Z }, "text", text));
            DeLog.Info("DE.S8a beacon " + action + ": GPS '" + GpsName + "' at SE " + TerrainTiles.V(se) + " (SN " + TerrainTiles.V(CrashSiteSn) + ")");
        }

        /// <summary>de.s8a beacon (dev): removes the GPS and the marker, so the next tick in the planet's gravity adds it again.</summary>
        public static Dictionary<string, object> Retrigger()
        {
            var r = new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", true } };
            MySession s = MySession.Static;
            IMyGpsCollection gpss = MyAPIGateway.Session != null ? MyAPIGateway.Session.GPS : null;
            int removed = 0;
            if (s != null && s.LocalHumanPlayer != null && gpss != null)
            {
                long identity = s.LocalHumanPlayer.Identity.IdentityId;
                List<IMyGps> list = gpss.GetGpsList(identity);
                if (list != null)
                    foreach (IMyGps g in list.ToArray())
                        if (g != null && g.Name == GpsName) { gpss.RemoveGps(identity, g); removed++; }
            }
            if (s != null && !string.IsNullOrEmpty(s.CurrentPath))
            {
                try { File.Delete(MarkerPath(s.CurrentPath)); } catch (Exception) { }
            }
            _done = false;
            _nextMs = 0;
            Retriggers++;
            Jsonl.WriteLine(S4Rows.Row("s8a.beacon", "tick", (long)GameState.GameplayTick, "action", "retrigger", "removed", removed));
            r["removed"] = (long)removed;
            r["line"] = "de.s8a beacon: re-armed (" + removed + " GPS removed, marker deleted); it is added again in DE_4546B's gravity";
            return r;
        }

        public static Dictionary<string, object> Status()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "on", On }, { "done", _done }, { "added", (long)Added }, { "shown", (long)Shown }, { "retriggers", (long)Retriggers }, { "text", LastText },
            };
        }
    }
}
