using System;
using System.Collections.Generic;
using System.Diagnostics;
using DeepEngineers.Wire;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using VRage;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// One statics host (CONTRACT §5.4 <c>format statics.host</c>, S7.N10): a plain <c>MyEntity</c> without a model,
 /// <c>Render.Visible</c> false, <c>Save = false</c> (never in a save; seeded failure S7.M8 sets it true), named
 /// <c>DE.Statics.&lt;bx&gt;.&lt;by&gt;.&lt;bz&gt;.e&lt;epoch&gt;[.p&lt;k&gt;]</c>, world matrix = translation o = F⁻¹(mean of its
 /// primitives' SN centres), identity rotation. Its body is SE's own static pattern (<c>MyEnvironmentSector</c>,
 /// <c>MyPhysicsHelper</c>): <c>new HkStaticCompoundShape(HkReferencePolicy.TakeOwnership)</c> + one
 /// <c>AddInstance(HkShape, Matrix)</c> per primitive (<c>HkBoxShape(half')</c> placed at F⁻¹(c) − o with the
 /// staticAxes rows; <c>HkCapsuleShape(A − o, B − o, r/s)</c>; <c>HkSphereShape(r/s)</c> at F⁻¹(c) − o) + <c>Bake</c>,
 /// then <c>new MyPhysicsBody(host, RigidBodyFlag.RBF_STATIC)</c>,
 /// <c>CreateFromCollisionObject(shape, Vector3.Zero, host.WorldMatrix, null, MyPhysics.CollisionLayers.StaticCollisionLayer)</c>,
 /// <c>IsStaticForCluster = true</c>, the shape's own reference released (the body holds it), <c>Physics.Enabled = true</c>.
 /// Built on the update thread over several ticks (≤ 256 primitives per tick within the statics budget).
 /// <para>Members: <c>HkStaticCompoundShape(HkReferencePolicy)</c>,
 /// <c>AddInstance(HkShape shape, Matrix transform)</c> → int, <c>Bake</c>, implicit <c>HkShape</c>
 /// <c>HkBoxShape(Vector3)</c>,
 /// <c>HkCapsuleShape(Vector3, Vector3, float)</c>, <c>HkSphereShape(float)</c> and their implicit <c>HkShape</c>
 /// <c>HkShape.RemoveReference</c>; <c>MyPhysicsBody(IMyEntity, RigidBodyFlag)</c>,
 /// <c>CreateFromCollisionObject(HkShape, Vector3, MatrixD, HkMassProperties?, int)</c>, <c>IsStaticForCluster</c>
 /// <c>MyEntity.Save</c>, <c>DisplayName</c>, <c>WorldMatrix</c>,
 /// <c>EntityId</c>, <c>Render.Visible</c>, <c>Physics</c>, <c>Close</c> (<c>MyEntity.cs</c>);
 /// <c>MyEntityIdentifier.AllocateId(…)</c>; <c>MyEntities.Add(MyEntity,
 /// bool)</c>; <c>MyPhysics.CollisionLayers.StaticCollisionLayer</c> = 13.</para>
 /// </summary>
    internal sealed class S7StaticsHost
    {
        /// <summary>Primitives added to the compound per tick at most (CONTRACT: ≤ 256 per tick).</summary>
        public const int AddsPerTick = 256;

        public readonly string Name;
        public readonly int Part;
        /// <summary>SE world origin of the host (F⁻¹ of the mean SN centre of its primitives).</summary>
        public readonly Vector3D Origin;
        private readonly SiteMap _site;
        private readonly StaticPrimitive[] _prims;
        private readonly int _start, _count;
        private HkStaticCompoundShape _compound;
        private bool _compoundMade;
        private int _next;
        private MyEntity _entity;
        private bool _closed;

        public int Added { get; private set; }
        /// <summary>Primitives dropped by the mapping (determinant check, degenerate data), counted (CONTRACT §2.3).</summary>
        public int Skipped { get; private set; }
        public string LastDrop { get; private set; }
        public double BuildMs { get; private set; }
        /// <summary>Review F22: the time of the single <c>Bake</c> call and of the raise (entity add + body), measured for the real run.</summary>
        public double BakeMs { get; private set; }
        public double RaiseMs { get; private set; }
        public string Error { get; private set; }

        /// <param name="name">the host name (<see cref="S7StaticsMath.HostName"/>)</param>
 /// <param name="part">host index within the batch (−1 when one host holds the batch)</param>
 /// <param name="site">the bound site map (F⁻¹, mapdir⁻¹)</param>
 /// <param name="prims">the batch's primitives as received (SN world)</param>
 /// <param name="start">first primitive of this host</param>
 /// <param name="count">primitives of this host</param>
        public S7StaticsHost(string name, int part, SiteMap site, StaticPrimitive[] prims, int start, int count)
        {
            Name = name; Part = part; _site = site; _prims = prims; _start = start; _count = count;
            double x = 0, y = 0, z = 0;
            for (int i = 0; i < count; i++) { x += prims[start + i].centreSn.x; y += prims[start + i].centreSn.y; z += prims[start + i].centreSn.z; }
            double n = Math.Max(1, count);
            Vec3 o = site.SnToSe(new Vec3(x / n, y / n, z / n));
            Origin = new Vector3D(o.X, o.Y, o.Z);
        }

        public bool Built { get { return _entity != null && !_closed; } }
        public bool Failed { get { return Error != null; } }
        public bool Closed { get { return _closed; } }
        public long EntityId { get { MyEntity e = _entity; return e != null ? e.EntityId : 0; } }
        public MyEntity Entity { get { return _entity; } }
        public int Count { get { return _count; } }

        /// <summary>
 /// Adds up to <paramref name="maxAdds"/> primitives while the stopwatch is below <paramref name="budgetMs"/>;
 /// when every primitive is in, bakes the compound and raises the host. Returns true when the host is up (or failed).
 /// </summary>
        public bool Step(int maxAdds, double budgetMs, Stopwatch sw, bool saveMutant)
        {
            if (_closed || Failed || _entity != null) return true;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                if (!_compoundMade)
                {
                    _compound = new HkStaticCompoundShape(HkReferencePolicy.TakeOwnership);
                    _compoundMade = true;
                }
                int adds = 0;
                while (_next < _count && adds < maxAdds && sw.Elapsed.TotalMilliseconds < budgetMs)
                {
                    S7StaticSe se = S7StaticsMath.ToSe(_site, ref _prims[_start + _next]);
                    AddOne(ref se);
                    _next++;
                    adds++;
                }
                if (_next < _count) return false;
                if (Added == 0) { Error = "no primitive survived the mapping"; ReleaseCompound(); return true; }
                long tb = Stopwatch.GetTimestamp();
                _compound.Bake();
                long tr = Stopwatch.GetTimestamp();
                Raise(saveMutant);
                long te = Stopwatch.GetTimestamp();
                BakeMs = (tr - tb) * 1000.0 / Stopwatch.Frequency;
                RaiseMs = (te - tr) * 1000.0 / Stopwatch.Frequency;
                return true;
            }
            catch (Exception e)
            {
                Error = e.GetType().Name + ": " + e.Message;
                ReleaseCompound();
                CloseEntity();
                return true;
            }
            finally
            {
                BuildMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            }
        }

        private void AddOne(ref S7StaticSe s)
        {
            if (!s.Ok) { Skipped++; LastDrop = s.Drop; return; }
            Vector3D o = Origin;
            switch (s.Kind)
            {
                case StaticKind.Capsule:
                {
                    var a = new Vector3((float)(s.A.X - o.X), (float)(s.A.Y - o.Y), (float)(s.A.Z - o.Z));
                    var b = new Vector3((float)(s.B.X - o.X), (float)(s.B.Y - o.Y), (float)(s.B.Z - o.Z));
                    if (Vector3.DistanceSquared(a, b) < 1e-8f)
                    {
                        // a capsule without a segment is a sphere (Havok refuses coincident end points)
                        var sph = new HkSphereShape((float)s.Radius);
                        _compound.AddInstance(sph, Matrix.CreateTranslation((a + b) * 0.5f));
                    }
                    else
                    {
                        var cap = new HkCapsuleShape(a, b, (float)s.Radius);
                        _compound.AddInstance(cap, Matrix.Identity);
                    }
                    break;
                }
                case StaticKind.Sphere:
                {
                    var sph = new HkSphereShape((float)s.Radius);
                    _compound.AddInstance(sph, Matrix.CreateTranslation(new Vector3((float)(s.Centre.X - o.X), (float)(s.Centre.Y - o.Y), (float)(s.Centre.Z - o.Z))));
                    break;
                }
                default: // Box, MeshObb
                {
                    var box = new HkBoxShape(new Vector3((float)s.Half.X, (float)s.Half.Y, (float)s.Half.Z));
                    SeMatrix ax = s.Axes;
                    var m = new Matrix(
                        (float)ax.Right.X, (float)ax.Right.Y, (float)ax.Right.Z, 0f,
                        (float)ax.Up.X, (float)ax.Up.Y, (float)ax.Up.Z, 0f,
                        (float)ax.Backward.X, (float)ax.Backward.Y, (float)ax.Backward.Z, 0f,
                        (float)(s.Centre.X - o.X), (float)(s.Centre.Y - o.Y), (float)(s.Centre.Z - o.Z), 1f);
                    _compound.AddInstance(box, m);
                    break;
                }
            }
            Added++;
        }

        private void Raise(bool saveMutant)
        {
            var e = new MyEntity();
            e.Save = saveMutant; // S7.N10: never in a save (S7.M8 sets it true on a throwaway copy)
            e.DisplayName = Name;
            e.WorldMatrix = MatrixD.CreateTranslation(Origin);
            e.EntityId = MyEntityIdentifier.AllocateId();
            try { e.Render.Visible = false; } catch (Exception) { }
            MyEntities.Add(e, true);
            _entity = e;
            var body = new MyPhysicsBody(e, RigidBodyFlag.RBF_STATIC);
            e.Physics = body;
            body.CreateFromCollisionObject(_compound, Vector3.Zero, e.WorldMatrix, null, MyPhysics.CollisionLayers.StaticCollisionLayer);
            body.IsStaticForCluster = true;
            HkShape shape = _compound;
            shape.RemoveReference(); // the rigid body holds its own reference (MyPhysicsHelper pattern)
            _compoundMade = false;
            e.Physics.Enabled = true;
        }

        private void ReleaseCompound()
        {
            if (!_compoundMade) return;
            _compoundMade = false;
            try { HkShape shape = _compound; shape.RemoveReference(); } catch (Exception) { }
        }

        private void CloseEntity()
        {
            MyEntity e = _entity;
            _entity = null;
            if (e == null) return;
            try { if (!e.Closed && !e.MarkedForClose) e.Close(); } catch (Exception) { }
        }

        /// <summary>Removes the host (entity, body, shapes) or the build in progress. Idempotent.</summary>
        public void Close()
        {
            if (_closed) return;
            _closed = true;
            ReleaseCompound();
            CloseEntity();
        }

    }
}
