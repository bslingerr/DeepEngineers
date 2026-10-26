using System;
using System.Collections.Generic;
using System.Text;
using DeepEngineers.Wire;

namespace DeepEngineers.SN
{
    /// <summary>One parsed C11 request: <c>{"id": n, "cmd": "&lt;name&gt;", "args": {…}}</c>.</summary>
    internal sealed class ControlRequest
    {
        public object Id;
        public string Cmd;
        public object Args;
    }

    /// <summary>Executes one request and returns the reply object (without the "id" field, which the consumer adds).</summary>
    internal interface IControlHandler
    {
        void Handle(ControlRequest request, JsonObjectWriter reply);
    }

    /// <summary>
 /// The Subnautica end of the test control channel (CONTRACT §5.1 C11, §5.4; ROADMAP §5.10): consumer of C11c
 /// CtlToSn, producer of C11d CtlFromSn. Dev builds only, and only while the header's `devControl` is non-zero (the
 /// caller checks both). Messages are type 1, one UTF-8 JSON object each. A reply that does not fit is dropped and
 /// counted (the plugin never waits). Corrupt counters or framing re-synchronise the consumer tail (tail:= head).
 /// At most <see cref="MaxMessagesPerFrame"/> requests are handled per frame (C11 is ≤ 100 messages/s).
 /// </summary>
    internal sealed unsafe class ControlConsumer
    {
        public const int MaxMessagesPerFrame = 16;

        private readonly ByteRing _in;
        private readonly ByteRing _out;
        private readonly byte[] _buffer;
        private readonly UTF8Encoding _utf8 = new UTF8Encoding(false, false);

        public long Received { get; private set; }
        public long Replied { get; private set; }
        public long BadType { get; private set; }
        public long BadJson { get; private set; }
        public long CorruptResyncs { get; private set; }
        public long ReplyDropped { get { return _out.Dropped; } }

        /// <summary>Build after the creator step (the ring objects read the head/tail words found in the section).</summary>
        public ControlConsumer(byte* sectionBase)
        {
            _in = new ByteRing(sectionBase, RingIndex.C11cCtlToSn);
            _out = new ByteRing(sectionBase, RingIndex.C11dCtlFromSn);
            _buffer = new byte[(int)_in.MaxPayloadBytes];
        }

        /// <summary>Consumer re-synchronisation of C11c (new SE generation, new link epoch).</summary>
        public void Resync() { _in.ResyncTail(); }

        public ulong InHead { get { return _in.Head; } }

        public void Poll(IControlHandler handler)
        {
            for (int n = 0; n < MaxMessagesPerFrame; n++)
            {
                uint type;
                int bytes;
                RingReadStatus st = _in.TryRead(out type, _buffer, out bytes);
                if (st == RingReadStatus.Empty) return;
                if (st != RingReadStatus.Ok)
                {
                    CorruptResyncs++;
                    DeLog.Warn("DE.Control C11c " + st + " (head=" + _in.Head + " tail=" + _in.Tail + "): consumer tail re-synchronised to the head");
                    _in.ResyncTail();
                    return;
                }
                Received++;
                if (type != C11Type.Json)
                {
                    BadType++;
                    DeLog.WarnOnce("c11-type-" + type, "DE.Control C11c message of type " + type + " ignored (only type 1 JSON)");
                    continue;
                }
                Dispatch(_utf8.GetString(_buffer, 0, bytes), handler);
            }
        }

        private void Dispatch(string text, IControlHandler handler)
        {
            var req = new ControlRequest();
            Dictionary<string, object> obj;
            try
            {
                obj = Json.Parse(text) as Dictionary<string, object>;
                if (obj == null) throw new FormatException("not a JSON object");
            }
            catch (Exception e)
            {
                BadJson++;
                DeLog.Warn("DE.Control bad request (" + e.Message + "): " + Clip(text));
                Reply(new JsonObjectWriter().Scalar("id", null).Bool("ok", false).Str("error", "bad JSON: " + e.Message).ToString());
                return;
            }
            object v;
            if (obj.TryGetValue("id", out v)) req.Id = v;
            if (obj.TryGetValue("cmd", out v)) req.Cmd = v as string;
            if (obj.TryGetValue("args", out v)) req.Args = v;

            var reply = new JsonObjectWriter().Scalar("id", req.Id);
            if (string.IsNullOrEmpty(req.Cmd))
            {
                reply.Bool("ok", false).Str("error", "missing \"cmd\"");
            }
            else
            {
                try
                {
                    handler.Handle(req, reply);
                }
                catch (Exception e)
                {
                    reply = new JsonObjectWriter().Scalar("id", req.Id).Bool("ok", false).Str("cmd", req.Cmd)
                        .Str("error", e.GetType().Name + ": " + e.Message);
                    DeLog.Warn("DE.Control " + req.Cmd + " threw " + e.GetType().Name + ": " + e.Message);
                }
            }
            Reply(reply.ToString());
        }

        /// <summary>Writes one reply on C11d, or drops and counts it when it does not fit.</summary>
        public bool Reply(string json)
        {
            byte[] bytes = _utf8.GetBytes(json);
            RingWriteStatus st = _out.TryWriteOrDrop(C11Type.Json, bytes, 0, bytes.Length);
            if (st == RingWriteStatus.Ok) { Replied++; return true; }
            DeLog.WarnOnce("c11d-" + st, "DE.Control C11d reply dropped (" + st + "); further drops are only counted");
            return false;
        }

        private static string Clip(string s)
        {
            if (s == null) return "";
            return s.Length <= 200 ? s : s.Substring(0, 200) + "...";
        }
    }
}
