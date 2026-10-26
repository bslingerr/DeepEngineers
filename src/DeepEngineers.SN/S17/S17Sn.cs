using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S17 (docs/S17-BRIEF.md; CONTRACT §5.4 "S17 conventions"), the Subnautica side: the C9 SnCreatures table filled with the
 /// nearby fish of the kind table (format c9.s17), SE's catch request read from the C5 block and handled once per reqSeq
 /// (format catch.sn: the fish's GameObject destroyed, no death, no loot), the answer in the C7 block (format c7.s17).
 /// Main thread, once per frame from Bridge.Update (S17 hook). Members VERIFIED <c>CraftData.GetTechType(GameObject)</c>
 /// <c>Creature.liveMixin</c>, <c>LiveMixin.IsAlive</c>,
 /// <c>MainCamera.camera</c>; Unity's <c>Physics.OverlapSphereNonAlloc</c> and <c>Object.Destroy</c>.
 /// Off with <c>DEEPENGINEERS_S17=off</c> (no C9 rows, no C7 block: the earlier behaviour); dev command <c>de.s17</c>.
 /// </summary>
    internal static unsafe class S17Sn
    {
        public const string Verb = "de.s17";
        public const string EnvVar = "DEEPENGINEERS_S17";
        private const int TerrainLayer = 30;
        public static readonly bool Enabled = ReadEnabled();

        private const int MaxCols = 4096;
        private static Collider[] _cols = new Collider[512];
        private static readonly SnCreature[] _out = new SnCreature[S17Conventions.C9.RowsMax];
        private static readonly HashSet<int> _seen = new HashSet<int>();
        private static readonly List<KeyValuePair<float, Creature>> _found = new List<KeyValuePair<float, Creature>>(64);
        private static readonly Dictionary<uint, Creature> _rows = new Dictionary<uint, Creature>();
        private static readonly byte[] _c9 = new byte[S17C9.Bytes];
        private static float _nextScanS;
        private static ulong _frame;
        private static uint _lastSeq;
        private static bool _haveLast, _inWorldLast;
        private static S17Ack _ack;
        private static float _nextErrorS;
        public static long Scans, RowsWritten, Handled, Caught, Gone, Moved, NotKind, Off, Errors, BufferFull, Contained, Tombstoned, EpochStale;
        public static int LastCount;
        // status every 5 s and a DE.S17 line every 30 s with the last scan's facts
        private static float _nextStatusS, _nextStatusLogS;
        private static int _lastColliders, _lastCreatures, _lastNotKind;
        private static string _lastNotKindName = "", _nearKind = "";
        private static float _nearDistM = -1f;

        private static bool ReadEnabled()
        {
            string v = null;
            try { v = Environment.GetEnvironmentVariable(EnvVar); } catch (Exception) { }
            return !string.Equals((v ?? "").Trim(), "off", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Bridge hook: once per frame. <paramref name="sectionBase"/> null while the section is closed. Never throws.</summary>
        public static void Frame(ulong nowMs, bool inWorld, bool seAlive, bool seStateValid, ref SeState st, byte* sectionBase)
        {
            if (!Enabled) return;
            try
            {
                _frame++;
                if (inWorld != _inWorldLast) { _rows.Clear(); _inWorldLast = inWorld; }
                S17Request req;
                if (seAlive && seStateValid && S17Wire.TryReadRequest(ref st, out req) && req.Active && (!_haveLast || req.ReqSeq != _lastSeq))
                {
                    _haveLast = true;
                    _lastSeq = req.ReqSeq;
                    Handle(ref req, inWorld, st.worldEpoch);
                }
                float now = Time.unscaledTime;
                if (now >= _nextStatusS) { _nextStatusS = now + 5f; StatusRow(inWorld, sectionBase != null, seAlive); }
                if (now >= _nextStatusLogS) { _nextStatusLogS = now + 30f; DeLog.Info("DE.S17 status: " + Status() + (inWorld ? "" : "; Subnautica not in its world") + (sectionBase == null ? "; section closed" : "")); }
                if (!inWorld || sectionBase == null) return;
                if (now < _nextScanS) return;
                _nextScanS = now + S17Conventions.C9.PeriodMs / 1000f;
                WriteC9(sectionBase);
            }
            catch (Exception e)
            {
                Errors++;
                float t = Time.unscaledTime;
                if (t >= _nextErrorS) { _nextErrorS = t + 10f; DeLog.Warn("DE.Error S17 frame " + e.GetType().Name + ": " + e.Message); }
            }
        }

        /// <summary>The RowsMax nearest fish of a kind row within RadiusCm of the camera, nearest first (format c9.s17); a dead fish
 /// (shot, knifed) floats and is catchable like a live one: the player grabs the body, no loot drop is involved.</summary>
        private static void WriteC9(byte* sectionBase)
        {
            Camera cam = MainCamera.camera;
            if (cam == null) return;
            Vector3 c = cam.transform.position;
            Scans++;
            _seen.Clear();
            _found.Clear();
            int n = Physics.OverlapSphereNonAlloc(c, S17Conventions.C9.RadiusCm / 100f, _cols, ~(1 << TerrainLayer), QueryTriggerInteraction.Collide);
            if (n >= _cols.Length)   //  a full buffer may hide fish (a grid's proxies, a base, kelp): counted, and the next scan has twice the room
            {
                BufferFull++;
                if (_cols.Length < MaxCols) _cols = new Collider[Math.Min(MaxCols, _cols.Length * 2)];
            }
            _lastColliders = n; _lastCreatures = 0; _lastNotKind = 0;
            for (int i = 0; i < n; i++)
            {
                Collider col = _cols[i];
                if (col == null) continue;
                Creature cr = col.GetComponentInParent<Creature>();
                if (cr == null || !_seen.Add(cr.gameObject.GetInstanceID())) continue;
                _lastCreatures++;
                S17Kind k;
                TechType tt = CraftData.GetTechType(cr.gameObject);
                if (!S17Conventions.TryKind((int)tt, out k)) { _lastNotKind++; _lastNotKindName = tt.ToString(); continue; }
                if (InContainment(cr)) { Contained++; continue; }   //  never a containment's fish
                _found.Add(new KeyValuePair<float, Creature>((cr.transform.position - c).sqrMagnitude, cr));
            }
            _found.Sort((a, b) => a.Key.CompareTo(b.Key));
            int count = Math.Min(_found.Count, S17Conventions.C9.RowsMax);
            _nearDistM = count > 0 ? Mathf.Sqrt(_found[0].Key) : -1f;
            if (count > 0) { S17Kind k0; _nearKind = S17Conventions.TryKind((int)CraftData.GetTechType(_found[0].Value.gameObject), out k0) ? (k0.Name ?? "") : ""; } else _nearKind = "";
            _rows.Clear();
            for (int i = 0; i < count; i++)
            {
                Creature cr = _found[i].Value;
                uint id = unchecked((uint)cr.gameObject.GetInstanceID());
                Vector3 p = cr.transform.position;
                Rigidbody rb = cr.GetComponent<Rigidbody>();
                Vector3 v = rb != null ? rb.velocity : Vector3.zero;
                _out[i] = new SnCreature
                {
                    instanceId = id, techType = (int)CraftData.GetTechType(cr.gameObject), posSn = new F32x3(p.x, p.y, p.z), velSn = new F32x3(v.x, v.y, v.z),
                    aggression = 0f, flags = S17Conventions.C9Flags.Catchable, targetEntityId = 0,
                };
                _rows[id] = cr;
            }
            S17C9.Write(Layout.RegionPtr(sectionBase, RegionId.C9SnCreatures), _c9, _out, count, _frame);
            LastCount = count;
            RowsWritten += count;
        }

        /// <summary>
 /// the status rows: every kind fish counted as "contained", 6,978 in one session, C9 always
 /// empty): <c>WaterParkCreature</c> sits on every small fish prefab (it is what lets a fish live in an Alien Containment), so its
 /// presence means nothing; only a fish inside a containment is one (<c>WaterParkCreature.IsInsideWaterPark</c>, present in the
 /// 22).
 /// </summary>
        private static bool InContainment(Creature cr)
        {
            WaterParkCreature w = cr.GetComponent<WaterParkCreature>();
            return w != null && w.IsInsideWaterPark();
        }

        /// <summary>One request, handled once (format catch.sn).</summary>
        private static void Handle(ref S17Request req, bool inWorld, uint recordEpoch)
        {
            Handled++;
            byte result;
            float dist = -1f;
            Creature cr;
            S17Kind k;
            bool spawnPoint = false;
            if (!inWorld) { result = S17Conventions.Result.Off; Off++; }
            else if (req.WorldEpoch != recordEpoch) { result = S17Conventions.Result.Gone; Gone++; EpochStale++; }   // a request of another SE world
            else if (!_rows.TryGetValue(req.InstanceId, out cr) || cr == null || cr.gameObject == null)
            { result = S17Conventions.Result.Gone; Gone++; }   // a dead fish still has its body: catchable
            else if (!S17Conventions.TryKind((int)CraftData.GetTechType(cr.gameObject), out k) || InContainment(cr)) { result = S17Conventions.Result.Kind; NotKind++; }
            else
            {
                Vector3 p = cr.transform.position;
                dist = Mathf.Sqrt((p.x - req.X) * (p.x - req.X) + (p.y - req.Y) * (p.y - req.Y) + (p.z - req.Z) * (p.z - req.Z));
                if (!S17Aim.WithinSlack(p.x - req.X, p.y - req.Y, p.z - req.Z)) { result = S17Conventions.Result.Moved; Moved++; }
                else
                {
                    _rows.Remove(req.InstanceId);
                    spawnPoint = S8aSpawns.OnCaught(cr.gameObject);   //  an S8a spawn point stays empty until the camera leaves it
                    if (spawnPoint) Tombstoned++;
                    UnityEngine.Object.Destroy(cr.gameObject);   // no death animation, no loot (CreatureDeath is never called)
                    result = S17Conventions.Result.Caught;
                    Caught++;
                }
            }
            _ack = new S17Ack { Version = (byte)S17Conventions.C7.Version, Result = result, AckSeq = req.ReqSeq, InstanceId = req.InstanceId, TechType = req.TechType };
            S17Kind kk;
            S17Conventions.TryKind(req.TechType, out kk);
            JsonOut j = S5Rows.Begin("s17.catch");
            j.Str("phase", "request").Num("seq", (long)req.ReqSeq).Num("instance", (long)req.InstanceId).Num("techType", (long)req.TechType).Str("kind", kk.Name ?? "")
             .Str("result", S17Conventions.Result.Name(result)).Num("distM", (double)dist).Bool("spawnPoint", spawnPoint);
            S5Rows.End(j);
            DeLog.Info("DE.S17 catch request " + req.ReqSeq.ToString(CultureInfo.InvariantCulture) + " " + (kk.Name ?? "?") + " instance " +
                       req.InstanceId.ToString(CultureInfo.InvariantCulture) + ": " + S17Conventions.Result.Name(result) +
                       (dist >= 0f ? " (" + dist.ToString("0.00", CultureInfo.InvariantCulture) + " m from SE's aim)" : ""));
        }

        /// <summary>Bridge hook (WriteStatus): the C7 S17 block (format c7.s17), every frame; nothing while S17 is off.</summary>
        public static void FillStatus(ref SnStatus s)
        {
            if (!Enabled) return;
            try { S17Ack a = _ack; a.Version = (byte)S17Conventions.C7.Version; S17Wire.WriteAck(ref s, ref a); }
            catch (Exception) { }
        }

        private static void StatusRow(bool inWorld, bool section, bool seAlive)
        {
            JsonOut j = S5Rows.Begin("s17.status");
            j.Bool("enabled", Enabled).Bool("inWorld", inWorld).Bool("section", section).Bool("seAlive", seAlive)
             .Num("scans", Scans).Num("lastCount", (long)LastCount).Num("rowsWritten", RowsWritten).Num("colliders", (long)_lastColliders).Num("creatures", (long)_lastCreatures)
             .Num("notKind", (long)_lastNotKind).Str("notKindName", _lastNotKindName).Str("nearKind", _nearKind).Num("nearDistM", (double)_nearDistM)
             .Num("bufferFull", BufferFull).Num("contained", Contained).Num("requests", Handled).Num("caught", Caught).Num("errors", Errors);
            S5Rows.End(j);
        }

        public static string Status()
        {
            return "S17 " + (Enabled ? "on" : "off (" + EnvVar + "=off)") + ", C9 rows " + LastCount + " (scans " + Scans + "), requests " + Handled + ", caught " + Caught +
                   ", last scan: colliders " + _lastColliders + ", creatures " + _lastCreatures + ", not a kind " + _lastNotKind + (_lastNotKind > 0 ? " (last " + _lastNotKindName + ")" : "") +
                   (_nearDistM >= 0f ? ", nearest " + _nearKind + " " + _nearDistM.ToString("0.0", CultureInfo.InvariantCulture) + " m" : ", no kind fish within 20 m") +
                   ", gone " + Gone + ", moved " + Moved + ", kind " + NotKind + ", off " + Off + ", tombstoned " + Tombstoned + ", contained " + Contained + ", epoch " + EpochStale + ", buffer full " + BufferFull + " (" + _cols.Length + "), last ack " + _ack.AckSeq + " " + S17Conventions.Result.Name(_ack.Result) +
                   ", errors " + Errors;
        }

        /// <summary><c>de.s17 {status}</c> (dev builds; chat <c>/de sn s17 status</c>).</summary>
        public static bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            if (req == null || req.Cmd != Verb) return false;
            if (!BuildFlavor.Dev) { reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", UserCommandRules.RefusalText); return true; }
            reply.Bool("ok", true).Str("cmd", req.Cmd).Bool("enabled", Enabled).Num("c9Rows", (long)LastCount).Num("scans", Scans).Num("requests", Handled)
                 .Num("caught", Caught).Num("gone", Gone).Num("moved", Moved).Num("kind", NotKind).Num("off", Off).Num("errors", Errors)
                 .Str("conventions", S17Conventions.Header + " 0x" + S17Conventions.Hash().ToString("x16", CultureInfo.InvariantCulture))
                 .Str("line", Status());
            return true;
        }
    }
}
