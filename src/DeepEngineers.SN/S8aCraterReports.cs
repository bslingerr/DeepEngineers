using System;
using System.Collections.Generic;
using System.IO;
using DeepEngineers.Wire;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S8a Stage E (CONTRACT §3.3 "Ores in the Subnautica map"; <see cref="S8aCraterOres"/>): the large resource deposits this
 /// Subnautica starts inside the Subnautica map, reported once each into the world's store folder (DepositsFile) for SE's
 /// tiles to put an ore pocket under them. Their positions exist only at run time
 /// <see cref="Report"/> runs on the main thread from the
 /// <c>Drillable.Start</c> postfix; <see cref="Flush"/> appends the pending lines once the S8a header named the store key
 /// (the first flush of a key reads the file's existing lines, so a deposit is never written twice).
 /// </summary>
    internal static class S8aCraterReports
    {
        private static readonly List<string> Pending = new List<string>();
        private static readonly HashSet<long> Seen = new HashSet<long>();
        private static ulong _keyLoaded;
        public static long Reported, Written, OutsideMap, Errors;

        public static void Report(GameObject go)
        {
            try
            {
                if (go == null) return;
                Vector3 p = go.transform.position;
                if (!S8aCraterOres.InMap(p.x, p.z)) { OutsideMap++; return; }
                string tech = CraftData.GetTechType(go).ToString();
                if (S8aCraterOres.RowOfTechType(tech) < 0) return;
                long k = S8aCraterOres.PosKey(p.x, p.y, p.z);
                lock (Pending)
                {
                    if (!Seen.Add(k)) return;
                    Pending.Add(S8aCraterOres.FormatLine(tech, p.x, p.y, p.z));
                    Reported++;
                }
            }
            catch (Exception e) { Errors++; DeLog.Warn("DE.S8a crater deposit report " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>Appends the pending deposits to the store key's file (main thread, from the S8a terrain tick).</summary>
        public static void Flush(ulong storeKey)
        {
            if (storeKey == 0) return;
            lock (Pending)
            {
                if (Pending.Count == 0 && _keyLoaded == storeKey) return;
                string path = S8aCraterOres.DepositsPath(S8aSnPaths.Root(), storeKey);
                try
                {
                    if (_keyLoaded != storeKey)
                    {
                        _keyLoaded = storeKey;
                        if (File.Exists(path))
                        {
                            var known = new HashSet<long>();
                            foreach (S8aCraterDeposit d in S8aCraterOres.Parse(File.ReadAllText(path))) known.Add(S8aCraterOres.PosKey(d.X, d.Y, d.Z));
                            Pending.RemoveAll(line => { S8aCraterDeposit d; return S8aCraterOres.TryParseLine(line, out d) && known.Contains(S8aCraterOres.PosKey(d.X, d.Y, d.Z)); });
                            Seen.UnionWith(known);
                        }
                    }
                    if (Pending.Count == 0) return;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    bool fresh = !File.Exists(path);
                    File.AppendAllText(path, (fresh ? S8aCraterOres.DepositsMagic + "\n" : "") + string.Join("\n", Pending.ToArray()) + "\n");
                    Written += Pending.Count;
                    DeLog.Info("DE.S8a crater deposits: " + Pending.Count + " reported to " + path + " (" + Written + " this session)");
                    Pending.Clear();
                }
                catch (Exception e) { Errors++; DeLog.Warn("DE.S8a crater deposits not written: " + e.GetType().Name + ": " + e.Message); }
            }
        }
    }
}
