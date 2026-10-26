using System;
using System.Collections.Generic;

namespace DeepEngineers.SN
{
    /// <summary>
 /// <c>de.spawn {techType, count 1..20, dist 1..100}</c> (or argv [techType, n, dist], the SE chat relay
 /// <c>/de spawn</c>): <c>DevConsole.SendConsoleCommand("spawn &lt;TechType&gt; &lt;n&gt; &lt;dist&gt;")</c>
 /// The TechType name is validated here ignoring case against the enum.
 /// </summary>
    internal static class SpawnRelay
    {
        private static List<string> _names;

        public static IList<string> TechTypeNames()
        {
            if (_names != null) return _names;
            var l = new List<string>();
            foreach (string n in Enum.GetNames(typeof(TechType))) l.Add(n);
            _names = l;
            return l;
        }

        /// <summary>Sends the console line; false (and the reason) when the console is not there or refused it.</summary>
 /// <summary>Review F1: the TechType names spawned this world (teardown destroys their creatures within 100 m).</summary>
        public static readonly HashSet<string> SpawnedTech = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static bool Send(string techType, int count, int dist, out string cmd, out string error)
        {
            cmd = SpawnArgs.Command(techType, count, dist);
            if (!string.IsNullOrEmpty(techType)) SpawnedTech.Add(techType.Trim());
            error = null;
            try
            {
                bool ok = DevConsole.SendConsoleCommand(cmd);
                if (!ok) error = "DevConsole.SendConsoleCommand returned false (no 'spawn' listener?)";
                return ok;
            }
            catch (Exception e)
            {
                error = "DevConsole.SendConsoleCommand: " + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }
    }
}
