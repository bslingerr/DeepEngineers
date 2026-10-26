using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>Outcome of the Unity half of the Subnautica WM-1 subset check.</summary>
    internal sealed class UnitySelfTestResult
    {
        public bool Pass;
        public int LocalCount, BoxCount, CornerChecks, Failures;
        public double WorstLocal, WorstLocalRoundTrip, WorstRoot, WorstRotation;
        public double WorstClosest, WorstClosestRepose, WorstBoundsCentre, WorstBoundsCentreRepose;
        /// <summary>Largest bounds extent minus the exact OBB extent (m), and the same relative to the extent.</summary>
        public double WorstBoundsExcess, WorstBoundsRelExcess;
        public string Mutants = "none";
        public string Message = "";

        public string Summary
        {
            get
            {
                var ci = CultureInfo.InvariantCulture;
                var sb = new StringBuilder(320);
                sb.Append("unity=").Append(Pass ? "PASS" : "FAIL");
                sb.Append(" local=").Append(LocalCount.ToString(ci));
                sb.Append(" box=").Append(BoxCount.ToString(ci));
                sb.Append(" corners=").Append(CornerChecks.ToString(ci));
                sb.Append(" worst=pos:").Append(DeLog.F(WorstLocal)).Append("m");
                sb.Append(",roundtrip:").Append(DeLog.F(WorstLocalRoundTrip)).Append("m");
                sb.Append(",root:").Append(DeLog.F(WorstRoot)).Append("m");
                sb.Append(",rot:").Append(DeLog.F(WorstRotation));
                sb.Append(",closest:").Append(DeLog.F(WorstClosest)).Append("m");
                sb.Append(",repose30:").Append(DeLog.F(WorstClosestRepose)).Append("m");
                sb.Append(",boundsCentre:").Append(DeLog.F(Math.Max(WorstBoundsCentre, WorstBoundsCentreRepose))).Append("m");
                sb.Append(",boundsExcess:").Append(DeLog.F(WorstBoundsExcess)).Append("m(rel ").Append(DeLog.F(WorstBoundsRelExcess)).Append(")");
                sb.Append(" tol=").Append(DeLog.F(UnitySelfTest.TolPosition)).Append("m");
                sb.Append(" mutants=").Append(Mutants);
                if (!Pass) sb.Append(" unityFailures=").Append(Failures.ToString(ci)).Append(" unityReason=").Append(Message);
                return sb.ToString();
            }
        }
    }

    /// <summary>
 /// The Unity half of the Subnautica in-game WM-1 self-check (CONTRACT §2.5, §2.6, §2.9; ROADMAP S0.G1-E4a). The
 /// embedded subset (Wire Wm1Subset, SITE_V1) is realised with real GameObjects in this runtime (Unity 2019.4 Mono),
 /// and Unity's own results are compared with the Python oracle's numbers:
 /// <list type="bullet">
 /// <item>50 'local' records: Frame (root; position o, rotation identity, localScale (s, 1, s)) → Entity (localRotation
 /// q, localScale 1) → probe (localPosition Mirror(l)). <c>probe.position</c> must equal the oracle's SN point, and
 /// Mirror(Entity.InverseTransformPoint(SN point)) must give l back.</item>
 /// <item>20 'box' records as real BoxColliders on unit-scale section roots (kinematic Rigidbody; position
 /// Apply(o, q, s, c_sec), rotation q), pre-scaled with K (WireMath.PrescaleBox): on the root for level boxes, on a
 /// unit-scale child for rotated ones. After Physics.SyncTransforms: the root position, the collider rotation,
 /// <c>Collider.ClosestPoint</c> at the 8 corners (probes 0.5 m outside each corner) and <c>Collider.bounds</c>
 /// against the oracle's pre-scaled box. Then every root is re-posed by 30° (rigid rotation about its position),
 /// synced again and the corners are checked again: a skipped SyncTransforms reads the stale pose (mutant
 /// sn.skipSyncTransforms), colliders under the scaled Frame cannot follow (mutant sn.collidersUnderFrame).</item>
 /// </list>
 /// Tolerance: 1 mm (CONTRACT §2.9 "after float32"); collider rotations 1e-5 (float32). <c>Collider.bounds</c> may be
 /// inflated by PhysX (up to 1 % plus the contact offset is accepted; the measured excess is logged); its centre is
 /// held to 1 mm. Physics.autoSyncTransforms is set to false for the duration of the check (as in game,
 /// MainGameController.StartGame) and restored. Every object is deactivated and destroyed before returning. Never throws.
 /// </summary>
    internal static class UnitySelfTest
    {
        public const double TolPosition = Wm1Subset.TolPositionFloat32;
        public const double TolRotation = 1e-5;
        public const double BoundsRelativeSlack = 0.0101;
        public const double ProbeOffset = 0.5;
        public const double ReposeDegrees = 30.0;

        /// <summary>Re-pose rotation: 30° about the unit axis (0.6, 0, 0.8) (tilts every box).</summary>
        private static Quat ReposeRotation()
        {
            double half = ReposeDegrees * Math.PI / 360.0;
            double sn = Math.Sin(half);
            return new Quat(0.6 * sn, 0.0, 0.8 * sn, Math.Cos(half));
        }

        private struct BoxCase
        {
            public Wm1BoxRecord Rec;
            /// <summary>The transform re-posed by 30° (the section root; under the mutant, the Entity).</summary>
            public Transform Posed;
            /// <summary>The transform whose world position is the section root position.</summary>
            public Transform Section;
            /// <summary>The transform that carries the BoxCollider.</summary>
            public Transform ColliderTransform;
            public BoxCollider Collider;
        }

        public static UnitySelfTestResult Run()
        {
            var r = new UnitySelfTestResult();
            bool skipSync = DevMutants.IsOn(DevMutants.SkipSyncTransforms);
            bool underFrame = DevMutants.IsOn(DevMutants.CollidersUnderFrame);
            r.Mutants = DevMutants.Describe();
            var created = new List<GameObject>(128);
            bool savedAutoSync = Physics.autoSyncTransforms;
            try
            {
                Physics.autoSyncTransforms = false;
                RunLocal(r, created);
                RunBoxes(r, created, skipSync, underFrame);
            }
            catch (Exception e)
            {
                r.Failures++;
                Fail(r, "exception " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                try { Physics.autoSyncTransforms = savedAutoSync; } catch (Exception) { }
                foreach (GameObject go in created)
                {
                    try
                    {
                        if (go == null) continue;
                        go.SetActive(false);
                        UnityEngine.Object.Destroy(go);
                    }
                    catch (Exception) { }
                }
            }
            r.Pass = r.Failures == 0 && r.Message.Length == 0 &&
                     r.LocalCount == Wm1Subset.SnLocalCount && r.BoxCount == Wm1Subset.SnBoxCount;
            return r;
        }

        // ------------------------------------------------------------------------------------------ local records

        private static void RunLocal(UnitySelfTestResult r, List<GameObject> created)
        {
            for (int i = 0; i < Wm1Subset.SnLocalCount; i++)
            {
                Wm1LocalRecord rec = Wm1Subset.SnLocal(i);
                var frame = new GameObject("DeepEngineers.SelfTest.Frame");
                created.Add(frame);
                Transform ft = frame.transform;
                ft.position = V(rec.Pose.O);
                ft.rotation = Quaternion.identity;
                ft.localScale = new Vector3((float)rec.Pose.S, 1f, (float)rec.Pose.S);

                var entity = new GameObject("DeepEngineers.SelfTest.Entity");
                Transform et = entity.transform;
                et.SetParent(ft, false);
                et.localPosition = Vector3.zero;
                et.localRotation = Q(rec.Pose.Q);
                et.localScale = Vector3.one;

                var probe = new GameObject("DeepEngineers.SelfTest.Probe");
                Transform pt = probe.transform;
                pt.SetParent(et, false);
                pt.localPosition = V(WireMath.Mirror(rec.LocalSe));

                double e = Vec3.Distance(D(pt.position), rec.ExpectedSn);
                double rt = Vec3.Distance(WireMath.Mirror(D(et.InverseTransformPoint(V(rec.ExpectedSn)))), rec.LocalSe);
                r.WorstLocal = Worse(r.WorstLocal, e);
                r.WorstLocalRoundTrip = Worse(r.WorstLocalRoundTrip, rt);
                if (!(e <= TolPosition && rt <= TolPosition))
                {
                    r.Failures++;
                    Fail(r, "local[" + i + "] position error " + DeLog.F(e) + " m, round trip " + DeLog.F(rt) + " m");
                }
                r.LocalCount++;
            }
        }

        // ------------------------------------------------------------------------------------------ box records

        private static void RunBoxes(UnitySelfTestResult r, List<GameObject> created, bool skipSync, bool underFrame)
        {
            var cases = new BoxCase[Wm1Subset.SnBoxCount];
            for (int i = 0; i < cases.Length; i++)
            {
                Wm1BoxRecord rec = Wm1Subset.SnBox(i);
                cases[i] = underFrame ? BuildUnderFrame(rec, created) : BuildSection(r, i, rec, created);
            }

            if (!skipSync) Physics.SyncTransforms();
            for (int i = 0; i < cases.Length; i++) CheckBox(r, i, cases[i], Quat.Identity, false);

            Quat rr = ReposeRotation();
            for (int i = 0; i < cases.Length; i++)
            {
                Quat q2 = rr * cases[i].Rec.Pose.Q;
                if (underFrame) cases[i].Posed.localRotation = Q(q2);
                else cases[i].Posed.rotation = Q(q2);
            }
            if (!skipSync) Physics.SyncTransforms();
            for (int i = 0; i < cases.Length; i++)
            {
                CheckBox(r, i, cases[i], rr, true);
                r.BoxCount++;
            }
        }

        /// <summary>
 /// The production placement (CONTRACT §2.6): unit-scale section root at Apply(o, q, s, c_sec) with rotation q
 /// and a kinematic Rigidbody; the box pre-scaled by K on the root (level/vertical axes) or on a unit-scale child
 /// (rotated). The S6 proxy code is expected to reuse exactly this placement.
 /// </summary>
        private static BoxCase BuildSection(UnitySelfTestResult r, int index, Wm1BoxRecord rec, List<GameObject> created)
        {
            WirePose pose = rec.Pose;
            Vec3 rootPos = WireMath.Apply(pose, rec.SectionCentreSe);
            ProxyBox box = WireMath.PrescaleBox(pose.Q, pose.S, rec.SectionCentreSe, rec.BoxCentreSe, rec.HalfExtentsSe,
                rec.AxisSe0, rec.AxisSe1, rec.AxisSe2);

            var root = new GameObject("DeepEngineers.SelfTest.Section");
            created.Add(root);
            Transform rt = root.transform;
            rt.SetPositionAndRotation(V(rootPos), Q(pose.Q));
            var body = root.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;

            bool onRoot = Quat.RotationDistance(box.Rotation, Quat.Identity) <= 1e-9;
            if (onRoot == rec.Rotated)
            {
                r.Failures++;
                Fail(r, "box[" + index + "] placed " + (onRoot ? "on the root" : "on a child") + " but the record says rotated=" + rec.Rotated);
            }
            var c = new BoxCase { Rec = rec, Posed = rt, Section = rt };
            Vector3 size = V(box.HalfExtents * 2.0);
            if (onRoot)
            {
                BoxCollider bc = root.AddComponent<BoxCollider>();
                bc.center = V(box.Centre);
                bc.size = size;
                c.Collider = bc;
                c.ColliderTransform = rt;
            }
            else
            {
                var child = new GameObject("DeepEngineers.SelfTest.Box");
                Transform ct = child.transform;
                ct.SetParent(rt, false);
                ct.localPosition = V(box.Centre);
                ct.localRotation = Q(box.Rotation);
                ct.localScale = Vector3.one;
                BoxCollider bc = child.AddComponent<BoxCollider>();
                bc.center = Vector3.zero;
                bc.size = size;
                c.Collider = bc;
                c.ColliderTransform = ct;
            }
            return c;
        }

        /// <summary>
 /// Mutant sn.collidersUnderFrame: the naive hierarchy CONTRACT §2.6 forbids. Frame (o, scale (s, 1, s)) →
 /// Entity (q) → section child at Mirror(c_sec) with a kinematic Rigidbody → unscaled boxes in Unity-local space.
 /// </summary>
        private static BoxCase BuildUnderFrame(Wm1BoxRecord rec, List<GameObject> created)
        {
            WirePose pose = rec.Pose;
            var frame = new GameObject("DeepEngineers.SelfTest.MutantFrame");
            created.Add(frame);
            Transform ft = frame.transform;
            ft.position = V(pose.O);
            ft.rotation = Quaternion.identity;
            ft.localScale = new Vector3((float)pose.S, 1f, (float)pose.S);

            var entity = new GameObject("DeepEngineers.SelfTest.MutantEntity");
            Transform et = entity.transform;
            et.SetParent(ft, false);
            et.localRotation = Q(pose.Q);

            var section = new GameObject("DeepEngineers.SelfTest.MutantSection");
            Transform st = section.transform;
            st.SetParent(et, false);
            st.localPosition = V(WireMath.Mirror(rec.SectionCentreSe));
            var body = section.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;

            Vec3 a0 = WireMath.Mirror(rec.AxisSe0), a1 = WireMath.Mirror(rec.AxisSe1), a2 = WireMath.Mirror(rec.AxisSe2);
            Quat rigid = Quat.FromColumns(a0, a1, -a2);
            var c = new BoxCase { Rec = rec, Posed = et, Section = st };
            var child = new GameObject("DeepEngineers.SelfTest.MutantBox");
            Transform ct = child.transform;
            ct.SetParent(st, false);
            ct.localPosition = V(WireMath.Mirror(rec.BoxCentreSe - rec.SectionCentreSe));
            ct.localRotation = Q(rigid);
            BoxCollider bc = child.AddComponent<BoxCollider>();
            bc.size = V(rec.HalfExtentsSe * 2.0);
            c.Collider = bc;
            c.ColliderTransform = ct;
            return c;
        }

        /// <summary>
 /// Checks one box against the oracle's pre-scaled collider, rigidly rotated by <paramref name="extra"/> about the
 /// section root when <paramref name="reposed"/>.
 /// </summary>
        private static void CheckBox(UnitySelfTestResult r, int index, BoxCase c, Quat extra, bool reposed)
        {
            Wm1BoxRecord rec = c.Rec;
            Quat q = extra * rec.Pose.Q;
            Vec3 rootExpected = rec.ExpectedSectionRootSn;
            string tag = (reposed ? "box30[" : "box[") + index + "]";

            double eRoot = Vec3.Distance(D(c.Section.position), rootExpected);
            r.WorstRoot = Worse(r.WorstRoot, eRoot);
            Quat rotExpected = q * rec.ExpectedRotationUnity;
            double eRot = Quat.RotationDistance(D(c.ColliderTransform.rotation).Normalized(), rotExpected);
            r.WorstRotation = Worse(r.WorstRotation, eRot);
            bool ok = eRoot <= TolPosition && eRot <= TolRotation;
            if (!ok) Fail(r, tag + " root error " + DeLog.F(eRoot) + " m, rotation error " + DeLog.F(eRot));

            var expected = new ProxyBox(rec.ExpectedCentreUnity, rec.ExpectedHalfExtentsUnity, rec.ExpectedRotationUnity,
                Vec3.Zero, Vec3.Zero, Vec3.Zero);
            Vec3 ax = rotExpected.Rotate(Vec3.UnitX), ay = rotExpected.Rotate(Vec3.UnitY), az = rotExpected.Rotate(Vec3.UnitZ);
            var min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
            var max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
            double worstCorner = 0;
            for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
            for (int sz = -1; sz <= 1; sz += 2)
            {
                Vec3 corner = rootExpected + q.Rotate(expected.CornerSectionLocal(sx, sy, sz));
                // CornerSectionLocal puts the corner at (sx*hx, sy*hy, -sz*hz) in the box frame: outward is (sx, sy, -sz).
                Vec3 probe = corner + (ax * sx + ay * sy - az * sz) * ProbeOffset;
                Vec3 cp = D(c.Collider.ClosestPoint(V(probe)));
                worstCorner = Worse(worstCorner, Vec3.Distance(cp, corner));
                min = new Vec3(Math.Min(min.X, corner.X), Math.Min(min.Y, corner.Y), Math.Min(min.Z, corner.Z));
                max = new Vec3(Math.Max(max.X, corner.X), Math.Max(max.Y, corner.Y), Math.Max(max.Z, corner.Z));
                r.CornerChecks++;
            }
            if (reposed) r.WorstClosestRepose = Worse(r.WorstClosestRepose, worstCorner);
            else r.WorstClosest = Worse(r.WorstClosest, worstCorner);
            if (!(worstCorner <= TolPosition))
            {
                ok = false;
                Fail(r, tag + " Collider.ClosestPoint corner error " + DeLog.F(worstCorner) + " m");
            }

            Bounds b = c.Collider.bounds;
            Vec3 centre = (min + max) * 0.5, extents = (max - min) * 0.5;
            double eCentre = Vec3.Distance(D(b.center), centre);
            if (reposed) r.WorstBoundsCentreRepose = Worse(r.WorstBoundsCentreRepose, eCentre);
            else r.WorstBoundsCentre = Worse(r.WorstBoundsCentre, eCentre);
            Vec3 got = D(b.extents);
            double slackAbs = c.Collider.contactOffset;
            bool extentsOk = true;
            for (int k = 0; k < 3; k++)
            {
                double excess = got[k] - extents[k];
                r.WorstBoundsExcess = Worse(r.WorstBoundsExcess, excess);
                if (extents[k] > 0.01) r.WorstBoundsRelExcess = Worse(r.WorstBoundsRelExcess, excess / extents[k]);
                if (!(excess >= -TolPosition && excess <= TolPosition + BoundsRelativeSlack * extents[k] + slackAbs)) extentsOk = false;
            }
            if (!(eCentre <= TolPosition) || !extentsOk)
            {
                ok = false;
                Fail(r, tag + " Collider.bounds centre error " + DeLog.F(eCentre) + " m, extents " + got + " vs " + extents);
            }
            if (!ok) r.Failures++;
        }

        // ------------------------------------------------------------------------------------------ helpers

        private static void Fail(UnitySelfTestResult r, string message)
        {
            if (r.Message.Length == 0) r.Message = message;
        }

        private static double Worse(double worst, double err)
        {
            return double.IsNaN(err) ? double.PositiveInfinity : Math.Max(worst, err);
        }

        private static Vector3 V(Vec3 v) { return new Vector3((float)v.X, (float)v.Y, (float)v.Z); }
        private static Quaternion Q(Quat q) { return new Quaternion((float)q.X, (float)q.Y, (float)q.Z, (float)q.W); }
        private static Vec3 D(Vector3 v) { return new Vec3(v.x, v.y, v.z); }
        private static Quat D(Quaternion q) { return new Quat(q.x, q.y, q.z, q.w); }
    }
}
