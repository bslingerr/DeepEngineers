using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S12 seeded failures of the Subnautica side (dev builds; tests/s12/analyze_s12.py): M6 the interior look preset off
 /// (S12.LOOK red), M7 no water plane drawn (S12.FLOOD.PLANE red). The SE side owns M1-M5.
 /// </summary>
    internal static class S12SnMutants
    {
        public const string LookOff = "S12.M6";
        public const string PlaneOff = "S12.M7";
        public static readonly string[] All = { LookOff, PlaneOff };
    }

    /// <summary>
 /// A flood row's starts (format flood.plane: one splash per startSeq increment). The first sight of a track counts as a
 /// start only when the track is fresh (rising from a level below <see cref="FreshLevel"/>); a new worldEpoch re-baselines.
 /// </summary>
    internal sealed class S12StartReader
    {
        public const double FreshLevel = 0.1;
        private bool _have;
        private uint _epoch, _last;
        public long Coalesced { get; private set; }

        public void Reset() { _have = false; }

        public int Step(uint epoch, uint seq, bool fresh)
        {
            if (!_have || epoch != _epoch)
            {
                _have = true;
                _epoch = epoch;
                _last = seq;
                return fresh && seq > 0 ? 1 : 0;
            }
            if (seq == _last) return 0;
            uint d = unchecked(seq - _last);
            _last = seq;
            if (d > 0x7FFFFFFFu) return 0;
            if (d > 1) Coalesced += d - 1;
            return 1;
        }
    }

    /// <summary>The shown water level eased toward the row's (format flood.plane: the level eased, ratePerS between records).</summary>
 /// <summary>
 /// de.s12's chat words (<c>/de sn s12 look off</c> arrives as <c>{"argv": ["look", "off"]}</c>): S3Args.ToDict keeps only

 /// 17:10: the look never changed). Game-free.
 /// </summary>
    internal static class S12SnArgs
    {
        public static void Words(object rawArgs, IDictionary<string, object> a)
        {
            var raw = rawArgs as IDictionary<string, object>;
            object v;
            if (raw == null || a == null || !raw.TryGetValue("argv", out v)) return;
            var l = v as IList;
            if (l == null) return;
            for (int i = 0; i + 1 < l.Count; i++)
            {
                string k = Convert.ToString(l[i], CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
                if (k != "look" && k != "water") continue;
                a[k] = Convert.ToString(l[i + 1], CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
                i++;
            }
        }
    }

    internal static class S12Ease
    {
        public const double MinSpeedPerS = 0.3;

        /// <summary>The next shown level: toward <paramref name="target"/> (extrapolated by the row's rate) at most max(|rate| x 1.5, MinSpeedPerS) per second.</summary>
        public static double Toward(double shown, double target, double ratePerS, double ageS, double dt)
        {
            double t = target + (double.IsNaN(ratePerS) ? 0 : ratePerS) * Math.Max(0, Math.Min(0.5, ageS));
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            if (double.IsNaN(shown)) return t;
            double speed = Math.Max(Math.Abs(double.IsNaN(ratePerS) ? 0 : ratePerS) * 1.5, MinSpeedPerS);
            double step = speed * Math.Max(0, dt);
            double d = t - shown;
            if (Math.Abs(d) <= step) return t;
            return shown + Math.Sign(d) * step;
        }
    }

    /// <summary>A per-vent stream of bubbles at <c>Vent.BubblesPerS</c> (fractions carried over frames).</summary>
    internal sealed class S12Stream
    {
        private double _carry;

        public int Take(double perSecond, double dt)
        {
            if (!(perSecond > 0) || !(dt > 0)) return 0;
            _carry += perSecond * Math.Min(dt, 0.25);
            int n = (int)Math.Floor(_carry);
            _carry -= n;
            return n;
        }

        public void Reset() { _carry = 0; }
    }

    /// <summary>SE-local box geometry on the Subnautica side (Unity-local = SE-local with z negated, as InteriorView's clip proxies).</summary>
    internal static class S12PlaneRules
    {
        /// <summary>
 /// The four corners (SE-local) of the water plane of <paramref name="b"/> at height <paramref name="h"/> along
 /// <paramref name="upAxis"/> (signed: format c6.s12), or false when the plane lies outside the box along the axis.
 /// </summary>
        public static bool Corners(CellBox b, double g, int upAxis, double h, double[] xyz12)
        {
            double floor, top;
            S12Boxes.FloorTop(b, g, upAxis, out floor, out top);
            if (!(h > floor) || !(h < top)) return false;
            double x0 = (b.MinX - 0.5) * g, x1 = (b.MaxX + 0.5) * g, y0 = (b.MinY - 0.5) * g, y1 = (b.MaxY + 0.5) * g, z0 = (b.MinZ - 0.5) * g, z1 = (b.MaxZ + 0.5) * g;
            bool neg = upAxis == S12Conventions.UpAxis.NegX || upAxis == S12Conventions.UpAxis.NegY || upAxis == S12Conventions.UpAxis.NegZ;
            double a = neg ? -h : h;
            for (int k = 0; k < 4; k++)
            {
                double u = (k & 1) != 0 ? 1 : 0, v = (k & 2) != 0 ? 1 : 0;
                double px, py, pz;
                switch (upAxis)
                {
                    case S12Conventions.UpAxis.PosX: case S12Conventions.UpAxis.NegX: px = a; py = y0 + u * (y1 - y0); pz = z0 + v * (z1 - z0); break;
                    case S12Conventions.UpAxis.PosZ: case S12Conventions.UpAxis.NegZ: px = x0 + u * (x1 - x0); py = y0 + v * (y1 - y0); pz = a; break;
                    default: px = x0 + u * (x1 - x0); py = a; pz = z0 + v * (z1 - z0); break;
                }
                xyz12[k * 3] = px; xyz12[k * 3 + 1] = py; xyz12[k * 3 + 2] = pz;
            }
            return true;
        }

        /// <summary>The plane height (SE-local metres along the up axis, signed) of a row's level over its union box.</summary>
        public static double Height(CellBox union, double g, int upAxis, double level)
        {
            double floor, top;
            S12Boxes.FloorTop(union, g, upAxis, out floor, out top);
            return floor + Math.Max(0, Math.Min(1, level)) * (top - floor);
        }

        public static double GridSize(byte code) { return code == 1 ? 0.5 : 2.5; }
    }
}
