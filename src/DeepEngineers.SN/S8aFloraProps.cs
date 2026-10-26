using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S8a.ND.props: one test object per family set on the seabed round a carve
 /// point, Subnautica's own prefabs (<c>CraftData.GetPrefabForTechTypeAsync</c>): a Pink Cap (TechType.PinkMushroom, a pickupable

 /// tubes (a large object). Pinned for the test: transient, kinematic, no FreezeRigidbodyWhenFar or WorldForces (an item near the
 /// camera would otherwise fall by itself and never test the rule). <c>de.s8a props {at: [x, y, z]}</c> sets them (asynchronously);
 /// <c>{status}</c> reports each: its family as S8aFlora sees it, its position, hidden or not; row <c>s8a.props</c>.
 /// </summary>
    internal static class S8aFloraProps
    {
        private static readonly string[][] Set =
        {
            new[] { "PinkMushroom", "0.0", "0.0" }, new[] { "ScrapMetal", "1.2", "0.0" }, new[] { "LimestoneChunk", "-1.2", "0.0" },
            new[] { "CoralShellPlate", "0.0", "1.2" }, new[] { "BigCoralTubes", "0.0", "-1.0" },
        };
        private sealed class Prop { public string Tech; public GameObject Go; public Vector3 At; public string Error; }
        private static readonly List<Prop> Props = new List<Prop>();
        private static int _pending;

        public static void Command(Dictionary<string, object> a, JsonObjectWriter reply, MonoBehaviour host)
        {
            object v;
            double x, y, z;
            if (a.TryGetValue("at", out v) && S3Args.TryVec(v, out x, out y, out z))
            {
                Clear();
                if (host == null) { reply.Bool("ok", false).Str("cmd", "de.s8a").Str("error", "props: no host for the prefab coroutines"); return; }
                foreach (string[] s in Set)
                {
                    var p = new Prop { Tech = s[0], At = new Vector3((float)(x + double.Parse(s[1], CultureInfo.InvariantCulture)), (float)y, (float)(z + double.Parse(s[2], CultureInfo.InvariantCulture))) };
                    Props.Add(p);
                    _pending++;
                    host.StartCoroutine(Spawn(p));
                }
                reply.Bool("ok", true).Str("cmd", "de.s8a").Str("action", "props").Num("spawning", (long)Props.Count);
                return;
            }
            if (S3Args.Flag(a, "clear", false)) Clear();
            var sb = new System.Text.StringBuilder();
            int hidden = 0, live = 0;
            foreach (Prop p in Props)
            {
                bool exists = p.Go != null;
                bool hid = exists && S8aFlora.IsHidden(p.Go);
                if (exists) live++;
                if (hid) hidden++;
                sb.Append(sb.Length > 0 ? ";" : "").Append(p.Tech).Append(':').Append(exists ? S8aFlora.Family(p.Go) : "-").Append(':').Append(hid ? 1 : 0).Append(':')
                  .Append(exists ? p.Go.transform.position.y.ToString("0.00", CultureInfo.InvariantCulture) : "nan").Append(':').Append(p.Error ?? "")
                  .Append(':').Append(exists ? S8aFlora.Diagnose(p.Go) : "");
            }
            reply.Bool("ok", true).Str("cmd", "de.s8a").Str("action", "props").Num("props", (long)Props.Count).Num("live", (long)live).Num("hidden", (long)hidden)
                 .Num("pending", (long)_pending).Str("list", sb.ToString());
            S8aTerrain.Row(new JsonOut().Str("t", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Num("tickMs", Win32.TickCount64())
                .Num("frame", DeLog.Frame).Str("level", "info").Str("event", "s8a.props").Str("ev", "s8a.props").Obj("data").Num("snFrame", DeLog.Frame)
                .Num("props", (long)Props.Count).Num("live", (long)live).Num("hidden", (long)hidden).Num("pending", (long)_pending).Str("list", sb.ToString())
                .Bool("m26", S8aMutants.M26).Num("registry", (long)S8aFlora.RegistryCount).Num("registryFound", S8aFlora.RegistryFound).Num("woken", S8aFlora.Woken).EndObj().ToString());
        }

        private static IEnumerator Spawn(Prop p)
        {
            TechType tt;
            try { tt = (TechType)Enum.Parse(typeof(TechType), p.Tech, true); }
            catch (Exception) { p.Error = "unknown TechType"; _pending--; yield break; }
            CoroutineTask<GameObject> task = CraftData.GetPrefabForTechTypeAsync(tt, false);
            yield return task;
            try
            {
                GameObject prefab = task.GetResult();
                if (prefab == null) { p.Error = "no prefab"; yield break; }
                // on the seabed: the terrain under the point (Subnautica's colliders: the camera is there for the test)
                Vector3 at = p.At;
                RaycastHit hit;
                if (Physics.Raycast(at + Vector3.up * 8f, Vector3.down, out hit, 40f, Voxeland.GetTerrainLayerMask(), QueryTriggerInteraction.Ignore)) at = hit.point;
                GameObject go = UWE.Utils.InstantiateDeactivated(prefab, at, Quaternion.identity);
                if (go == null) { p.Error = "instantiate"; yield break; }
                go.name = "DE.S8a.TestProp." + p.Tech;
                FreezeRigidbodyWhenFar frz = go.GetComponent<FreezeRigidbodyWhenFar>();
                if (frz != null) UnityEngine.Object.DestroyImmediate(frz);
                WorldForces wf = go.GetComponent<WorldForces>();
                if (wf != null) wf.enabled = false;
                Rigidbody rb = go.GetComponent<Rigidbody>();
                if (rb != null) rb.isKinematic = true;
                LargeWorldEntity lwe = go.GetComponent<LargeWorldEntity>();
                if (lwe == null) lwe = go.AddComponent<LargeWorldEntity>();
                lwe.cellLevel = LargeWorldEntity.CellLevel.Near;
                go.SetActive(true);
                try { LargeWorldStreamer lws = LargeWorldStreamer.main; if (lws != null) lws.MakeEntityTransient(go); } catch (Exception) { }
                // a test run (S8a.ND.props "cand 0 reg 0"): this dev spawn registers the prop itself (path "dev")
                S8aFlora.Register(lwe, "dev");
                p.Go = go;
            }
            catch (Exception e) { p.Error = e.GetType().Name; }
            finally { _pending--; }
        }

        public static void Clear()
        {
            foreach (Prop p in Props) { try { if (p.Go != null) UnityEngine.Object.Destroy(p.Go); } catch (Exception) { } }
            Props.Clear();
        }
    }
}
