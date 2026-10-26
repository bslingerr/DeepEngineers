using System;
using DeepEngineers.Wire;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The character section of CONTRACT §2.6 (kind 2, section id 0): a standalone root (never a child of the Player:
 /// <c>EnterLockedMode</c> disables the Player's capsule and its collisions) with its own kinematic Rigidbody and a
 /// non-trigger CapsuleCollider (radius 0.25 m, height 1.8 m, direction Y) on a unit-scale child, posed from C1 alone
 /// (no SkinSet for a character in v1): the root at <c>Apply(charO, charQ, charS, pelvis)</c> with rotation charQ, the
 /// capsule's axis pelvis → head (<see cref="CollisionSkinMath.CharacterCapsule"/> and
 /// <see cref="CollisionSkinMath.CharacterCapsuleWorld"/>; (0, 0.9, 0) and up without valid bones). The pose is the character mirror's of the same camera sample (CharacterMirror.LastPose/LastFrame, a
 /// minimal S5 hook), else C1's newest frame. Disabled while C1 says the character is absent, dead or seated. The
 /// same components as a grid section (LiveMixin + data, zero modifier, EcoTarget Shark, marker). S6.M8 puts the
 /// capsule on a child of the locked Player instead (no contacts).
 /// </summary>
    internal sealed class CharacterProxy
    {
        private GameObject _root, _capsuleGo;
        private Transform _t;
        private Rigidbody _body;
        private LiveMixin _live;
        private CapsuleCollider _capsule;
        private bool _active, _onPlayer;
        public ProxyMarker Marker { get; private set; }
        public bool HasTarget { get; private set; }
        public bool Present { get; private set; }
        public bool Dead { get; private set; }
        public bool Seated { get; private set; }
        public ulong CharacterId { get; private set; }
        public Vec3 TargetPos { get; private set; }
        public Quat TargetRot { get; private set; }
        public Vec3 O { get; private set; }
        public Quat Q { get; private set; }
        public double S { get; private set; }
        public ulong Tick { get; private set; }
        public bool BonesValid { get; private set; }
        public string Source = "none";
        private Quat _capsuleRot = Quat.Identity;
        private bool _hasPrev;
        private Vec3 _prevPos;
        private Quat _prevRot = Quat.Identity;
        public double LastMovedSec = double.NegativeInfinity;
        public long Teleports, Samples, Creates;

        public bool Active { get { return _active; } }

        /// <summary>The ghost rule's "Player at the character" (CONTRACT §4.2): present, not dead, not seated.</summary>
        public bool AtCharacter { get { return Present && !Dead && !Seated; } }

        public void Sample(CharacterMirror cm, double nowSec)
        {
            Samples++;
            CharFrame f = null;
            EntityPose pose = default(EntityPose);
            bool have = false;
            if (cm != null && cm.LastPoseValid && cm.LastFrame != null)
            {
                f = cm.LastFrame;
                pose = cm.LastPose;
                have = pose.Finite;
                Source = "mirror";
            }
            else if (cm != null)
            {
                f = cm.C1.Newest;
                if (f != null && f.PoseFinite)
                {
                    pose = new EntityPose { Id = f.CharacterId, Ox = f.Ox, Oy = f.Oy, Oz = f.Oz, Qx = f.Qx, Qy = f.Qy, Qz = f.Qz, Qw = f.Qw, S = f.S, K = 1 };
                    have = true;
                    Source = "c1";
                }
            }
            Present = f != null && f.CharacterId != 0 && f.Has(SeFrameFlags.CharacterPresent);
            Dead = f != null && f.Has(SeFrameFlags.CharacterDead);
            Seated = f != null && f.Has(SeFrameFlags.CharacterSeated);
            CharacterId = f != null ? f.CharacterId : 0UL;
            if (!have || !AtCharacter) { HasTarget = false; return; }
            Vec3 c, axis;
            CollisionSkinMath.CharacterCapsule(f.Bones, f.BonesValid, out c, out axis);
            BonesValid = f.BonesValid;
            O = pose.O; Q = pose.Q; S = pose.S; Tick = f.Tick;
            Vec3 pos;
            Quat capsule;
            CollisionSkinMath.CharacterCapsuleWorld(O, Q, S, c, axis, out pos, out capsule);
            _capsuleRot = capsule;
            if (!pos.IsFinite) { HasTarget = false; return; }
            if (_hasPrev && MotionRule.Moved(_prevPos, _prevRot, pos, Q)) { LastMovedSec = nowSec; _prevPos = pos; _prevRot = Q; }   // review F5: anchor pose
            if (!_hasPrev) { _prevPos = pos; _prevRot = Q; _hasPrev = true; }
            TargetPos = pos;
            TargetRot = Q;
            HasTarget = true;
        }

        private void Ensure(ProxySections registry)
        {
            if (_root != null) return;
            _root = new GameObject("DE.Proxy character");
            _root.SetActive(false);
            _root.layer = 0;
            Object.DontDestroyOnLoad(_root);
            _t = _root.transform;
            _body = ProxySections.AddBody(_root);
            _live = ProxySections.AddLive(_root);
            ProxySections.AddEco(_root);
            Marker = _root.AddComponent<ProxyMarker>();
            Marker.Kind = DamageTargetKind.Character;
            Marker.SectionId = 0;
            Marker.Live = _live;
            _capsuleGo = new GameObject("capsule");
            _capsuleGo.layer = 0;
            _capsuleGo.transform.SetParent(_t, false);
            _capsule = _capsuleGo.AddComponent<CapsuleCollider>();
            _capsule.radius = CombatConventions.Limits.CapsuleRadiusMm / 1000f;
            _capsule.height = CombatConventions.Limits.CapsuleHeightMm / 1000f;
            _capsule.direction = 1;
            _capsule.center = Vector3.zero;
            Marker.Colliders.Add(_capsule);
            registry.Register(Marker, _live, _root);
            Creates++;
            DeLog.Info("DE.S6 character section created (capsule r=0.25 h=1.8 on a unit child of a standalone root; kinematic body)");
        }

        /// <summary>The physics step: posed like a grid section; the capsule axis follows pelvis → head.</summary>
        public void Step(ProxySections registry)
        {
            if (!HasTarget)
            {
                if (_active && _root != null) { _root.SetActive(false); _active = false; }
                return;
            }
            Ensure(registry);
            bool onPlayer = S6Mutants.IsOn(S6Mutants.CapsuleOnPlayer);
            if (onPlayer != _onPlayer) Reparent(onPlayer);
            bool trig = S6Mutants.IsOn(S6Mutants.Triggers);
            if (_capsule.isTrigger != trig) _capsule.isTrigger = trig; // S6.M1
            if (!_onPlayer)
                _capsuleGo.transform.localRotation = ProxySections.Q((TargetRot.Conjugate() * _capsuleRot).Normalized()); // +Y along S·q·Mirror(pelvis→head)
            Vector3 tp = ProxySections.V(TargetPos);
            Quaternion tq = ProxySections.Q(TargetRot);
            if (!_active)
            {
                _t.SetPositionAndRotation(tp, tq);
                _body.position = tp;
                _body.rotation = tq;
                _root.SetActive(true);
                _active = true;
            }
            else
            {
                Vector3 bp = _body.position;
                Quaternion bq = _body.rotation;
                if (MotionRule.Teleport(ProxySections.D(bp), new Quat(bq.x, bq.y, bq.z, bq.w), TargetPos, TargetRot))
                {
                    _body.position = tp;
                    _body.rotation = tq;
                    Teleports++;
                }
                else
                {
                    _body.MovePosition(tp);
                    _body.MoveRotation(tq);
                }
            }
            Marker.EntityId = CharacterId;
            Marker.RootEntityId = CharacterId;
            Marker.StepO = O; Marker.StepQ = Q; Marker.StepS = S; Marker.StepRel = InstanceMath.Identity; Marker.StepTick = Tick; Marker.StepValid = true;
        }

        /// <summary>S6.M8: the capsule under the locked Player (whose collisions are off) or back on our own root.</summary>
        private void Reparent(bool onPlayer)
        {
            Player p = Player.main;
            if (onPlayer && p == null) return;
            _onPlayer = onPlayer;
            if (onPlayer)
            {
                _capsuleGo.transform.SetParent(p.transform, false);
                _capsuleGo.transform.localPosition = Vector3.zero;
                _capsuleGo.transform.localRotation = Quaternion.identity;
                DeLog.Warn("DE.S6 MUTANT S6.M8: the character capsule is a child of the locked Player (no contacts expected)");
            }
            else
            {
                _capsuleGo.transform.SetParent(_t, false);
                _capsuleGo.transform.localPosition = Vector3.zero;
                DeLog.Info("DE.S6 character capsule back on its own root");
            }
        }

        public void Destroy(ProxySections registry)
        {
            if (registry != null) registry.Unregister(_live, _root);
            if (_capsuleGo != null) Object.Destroy(_capsuleGo);
            if (_root != null) Object.Destroy(_root);
            _root = null; _capsuleGo = null; _capsule = null; _live = null; Marker = null; _active = false; _onPlayer = false;
            HasTarget = false;
        }
    }
}
