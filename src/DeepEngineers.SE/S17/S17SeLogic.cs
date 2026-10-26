using System;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The C5 S17 request block's current value (CONTRACT §5.4 "S17 conventions", <c>format c5.s17</c>): set by <c>S17Se</c>
 /// on the game thread, written into each C5 record by <c>Link.WriteC5</c> (same thread; S17 hook). Game-free so the link
 /// tests compile it; inactive = version 0 (no block, the earlier behaviour).
 /// </summary>
    internal static class S17SeWire
    {
        private static S17Request _req;
        private static bool _active;
        public static long Writes;
        /// <summary>The reqSeq of the last Active request written into a C5 record.</summary>
        public static uint PublishedSeq;

        public static bool IsActive { get { return _active; } }

        /// <summary>The last request (its Active flag set while it waits for the ack); a default request = none yet.</summary>
        public static void Set(S17Request r)
        {
            r.Version = (byte)S17Conventions.C5.Version;
            _req = r;
            _active = true;
        }

        public static void Off() { _active = false; _req = default(S17Request); }

        /// <summary>The S17 hook of <c>Link.WriteC5</c>: the block's bytes of this record (cleared when inactive).</summary>
        public static void Fill(ref SeState st)
        {
            if (!_active) { S17Wire.ClearRequest(ref st); return; }
            S17Request r = _req;
            S17Wire.WriteRequest(ref st, ref r);
            if (r.Active) PublishedSeq = r.ReqSeq;
            Writes++;
        }
    }

    /// <summary>The S17 switch read once per session (env).</summary>
    internal static class S17Switch
    {
        public const string EnvVar = "DEEPENGINEERS_S17";

        /// <summary>DEEPENGINEERS_S17=off disables the whole slice on SE (no catch, no C5 block); default on.</summary>
        public static readonly bool Enabled = Read();

        private static bool Read()
        {
            string v = null;
            try { v = Environment.GetEnvironmentVariable(EnvVar); } catch (Exception) { }
            return !string.Equals((v ?? "").Trim(), "off", StringComparison.OrdinalIgnoreCase);
        }
    }
}
