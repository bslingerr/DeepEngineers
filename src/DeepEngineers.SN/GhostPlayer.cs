using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The ghost Player (ROADMAP S1 SN plugin; CONTRACT §1 "Player movement"; ARCHITECTURE §5.11), the
 /// <c>FreecamController</c> pattern re-implemented
 /// <list type="bullet">
 /// <item>Setup: dummy handler pushed (<c>InputHandlerStack.main.Push(GameObject)</c>), <c>Player.main.EnterLockedMode(null)</c>
 /// (motors off, rigidbody kinematic, Mode.LockedPiloting), <c>FreezeStats</c>, <c>PDACameraFOVControl.enabled = false</c>
 /// (the component <c>Player.Start</c> adds; never destroyed: its OnDestroy resyncs the options FOV), every renderer
 /// under the Player hidden (body, head, first-person arms of the view model), and the Player moved out of the
 /// lifepod: placed at the camera, then <c>Player.OnPlayerPositionCheat</c> (VERIFIED: clears currentEscapePod,
 /// escapePod, currentSub, the water park, precursorOutOfWater and the water plane override; the game's own warp
 /// path).</item>
 /// <item>Every frame (LateUpdateLast): <c>GameInput.ClearInput(1)</c> (this frame and the next, so the next frame's
 /// early readers are covered), the Player root moved to the sample's target, hidden renderers kept disabled. Nothing moves while the drive gate holds.</item>
 /// <item>After the first frame: `escapePod == false` and `currentSub == null` asserted (logged). At the first frame
 /// C1 drives the Player, its position is asserted too: `DE.Ghost assert driven …`. Every second:
 /// <c>IsFrozenStats</c> asserted (re-frozen and counted if not), the dummy handler re-pushed when the avatar's
 /// default handler is focused again, renderers re-scanned, PDACameraFOVControl kept disabled, and a ghost found back
 /// in the lifepod or a sub (a respawn, a trigger) moved out again with OnPlayerPositionCheat (counted, logged).</item>
 /// <item>Health never changes (CONTRACT §4.2, which allows <c>LiveMixin.shielded</c>): <c>Player.main.liveMixin.shielded
 /// = true</c> (VERIFIED: TakeDamage then applies 0 and still notifies its receivers with the original damage), so a
 /// bite cannot kill the ghost during S1 runs (death would respawn it in the lifepod). S6 adds the damage capture.</item>
 /// </list>
 /// Seeded failures: S1.M6 leaves PDACameraFOVControl enabled; S1.M10 never moves the Player (lifepod state kept).
 /// </summary>
    internal sealed class GhostPlayer
    {
        private GameObject _inputDummy;
        private Renderer[] _hidden = new Renderer[0];

        /// <summary>
 /// S5a: renderers the ghost rule leaves alone while set (the first-person arms of the view model and the scuba
 /// mask, enabled by <see cref="S5aHost"/> only with C1 inWater and the camera under the sea; CONTRACT §5.4 "C1
 /// swim extension"). Null = every renderer under the Player stays hidden (S1 behaviour). The exempt renderers are
 /// still re-scanned, so clearing the set hides them again within a frame.
 /// </summary>
        public HashSet<Renderer> Exempt;

        public bool Ready { get; private set; }
        public bool Asserted { get; private set; }
        public bool AssertOk { get; private set; }
        /// <summary>The first C1-driven move was checked (position within 0.05 m of the target, flags clear).</summary>
        public bool DrivenAsserted { get; private set; }
        public bool DrivenAssertOk { get; private set; }
        public const float DrivenToleranceM = 0.05f;
        public bool AtPelvis { get; private set; }
        public Vector3 Target { get; private set; }
        public long Moves, RenderersHidden, StatsRefrozen, InputRepushed, FovControlRedisabled, MovedOutAgain, ShieldReasserted;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Setup(Transform parentForDummy, Vector3 cameraPos)
        {
            Player p = Player.main;
            if (p == null) throw new InvalidOperationException("Player.main is null");

            _inputDummy = new GameObject("DeepEngineers.InputDummy");
            if (parentForDummy != null) _inputDummy.transform.SetParent(parentForDummy, false);
            PushDummy();
            p.EnterLockedMode(null);
            if (!p.IsFrozenStats()) p.FreezeStats();

            PDACameraFOVControl fc = p.GetComponent<PDACameraFOVControl>();
            bool fovLeftOn = DevMutants.IsOn(DevMutants.S1FovControlLeftOn);
            if (fc != null && !fovLeftOn) fc.enabled = false;

            HideRenderers();
            if (p.liveMixin != null) p.liveMixin.shielded = true;

            bool notMoved = DevMutants.IsOn(DevMutants.S1PlayerNotMoved);
            Vector3 before = p.transform.position;
            bool podBefore = p.escapePod.value;
            string subBefore = p.currentSub != null ? p.currentSub.name : "null";
            if (!notMoved)
            {
                p.transform.position = cameraPos;
                p.OnPlayerPositionCheat();
            }
            Ready = true;
            DeLog.Info("DE.Ghost setup: EnterLockedMode(null) mode=" + p.GetMode() + " frozenStats=" + p.IsFrozenStats() +
                       " input dummy pushed, PDACameraFOVControl " + (fc == null ? "absent" : fovLeftOn ? "LEFT ENABLED (MUTANT S1.M6)" : "disabled") +
                       ", renderers hidden=" + _hidden.Length + ", liveMixin " + (p.liveMixin != null ? "shielded" : "absent") + "; Player " + CameraDriver.V(before) + " escapePod=" + podBefore + " currentSub=" + subBefore +
                       (notMoved ? " NOT MOVED (MUTANT S1.M10)" : " -> " + CameraDriver.V(cameraPos) + " (OnPlayerPositionCheat: out of the lifepod)"));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void PushDummy()
        {
#pragma warning disable 618 // InputHandlerStack.Push(GameObject) is marked obsolete; it is the FreecamController path (VERIFIED)
            InputHandlerStack.main.Push(_inputDummy);
#pragma warning restore 618
        }

        /// <summary>
 /// Per frame (LateUpdateLast, after the camera was placed). <paramref name="driven"/>: the camera was driven this
 /// frame (C1 or the override); false while there is no C1 or the drive gate holds: the Player is not moved.
 /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void PerFrame(CamSample s, Vector3 cameraPos, bool driven, bool overrideActive)
        {
            if (!Ready) return;
            GameInput.ClearInput(1);
            Player p = Player.main;
            if (p == null) return;
            bool notMoved = DevMutants.IsOn(DevMutants.S1PlayerNotMoved);
            if (driven && !notMoved)
            {
                bool pelvis = !overrideActive && s.PlayerAtPelvis;
                Vector3 t = pelvis ? new Vector3((float)s.PlayerX, (float)s.PlayerY, (float)s.PlayerZ) : cameraPos;
                AtPelvis = pelvis;
                Target = t;
                p.transform.position = t;
                Moves++;
            }
            if (driven && !overrideActive && !DrivenAsserted) AssertDriven(p, cameraPos, notMoved);
            HashSet<Renderer> exempt = Exempt;
            for (int i = 0; i < _hidden.Length; i++)
            {
                Renderer r = _hidden[i];
                if (r != null && r.enabled && (exempt == null || !exempt.Contains(r))) { r.enabled = false; RenderersHidden++; }
            }
        }

        /// <summary>The check of the frame after setup: escapePod false and currentSub null (S1.N8, S1.M10).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void AssertAfterFirstFrame()
        {
            if (Asserted || !Ready) return;
            Asserted = true;
            Player p = Player.main;
            bool pod = p.escapePod.value;
            bool sub = p.currentSub != null;
            AssertOk = !pod && !sub;
            string line = "DE.Ghost assert escapePod=" + (pod ? "true" : "false") + " currentSub=" + (sub ? p.currentSub.name : "null") +
                          " insideWalkable=" + p.IsInsideWalkable() + " canBeAttacked=" + p.CanBeAttacked() + " mode=" + p.GetMode();
            if (AssertOk) DeLog.Info(line + " OK");
            else DeLog.Warn(line + " FAIL (S1.N8: the ghost must be outside the lifepod and any sub)");
        }

        /// <summary>
 /// The first C1-driven frame (S1.N8, S1.M10): the Player root within 0.05 m of its target (the pelvis or the camera)
 /// and escapePod/currentSub clear. Under S1.M10 the Player is never moved, so this fails at the save's spot.
 /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AssertDriven(Player p, Vector3 cameraPos, bool notMoved)
        {
            DrivenAsserted = true;
            Vector3 target = notMoved ? cameraPos : Target;
            Vector3 pos = p.transform.position;
            float dist = (pos - target).magnitude;
            bool pod = p.escapePod.value;
            bool sub = p.currentSub != null;
            DrivenAssertOk = dist <= DrivenToleranceM && !pod && !sub;
            string line = "DE.Ghost assert driven: Player " + CameraDriver.V(pos) + " target " + CameraDriver.V(target) + " (" + (AtPelvis ? "pelvis" : "camera") +
                          ") dist=" + dist.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "m escapePod=" + (pod ? "true" : "false") +
                          " currentSub=" + (sub ? p.currentSub.name : "null") + " insideWalkable=" + p.IsInsideWalkable() +
                          (notMoved ? " (MUTANT S1.M10: Player never moved)" : "");
            if (DrivenAssertOk) DeLog.Info(line + " OK");
            else DeLog.Warn(line + " FAIL (S1.N8: the ghost must sit at its target, outside the lifepod and any sub)");
        }

        /// <summary>Per second.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void PerSecond()
        {
            if (!Ready) return;
            Player p = Player.main;
            if (p == null) return;
            if (!p.IsFrozenStats())
            {
                p.FreezeStats();
                StatsRefrozen++;
                DeLog.Warn("DE.Ghost stats were not frozen: FreezeStats() again (#" + StatsRefrozen + ")");
            }
            InputHandlerStack stack = InputHandlerStack.main;
            if (stack != null && stack.IsDefaultHandlerFocused())
            {
                PushDummy();
                InputRepushed++;
                DeLog.Warn("DE.Ghost the avatar input handler was focused again: dummy re-pushed (#" + InputRepushed + ")");
            }
            if (!DevMutants.IsOn(DevMutants.S1FovControlLeftOn))
            {
                PDACameraFOVControl fc = p.GetComponent<PDACameraFOVControl>();
                if (fc != null && fc.enabled) { fc.enabled = false; FovControlRedisabled++; }
            }
            if (p.liveMixin != null && !p.liveMixin.shielded)
            {
                p.liveMixin.shielded = true;
                ShieldReasserted++;
                DeLog.Warn("DE.Ghost liveMixin was unshielded: shielded again (#" + ShieldReasserted + ")");
            }
            if (Asserted && !DevMutants.IsOn(DevMutants.S1PlayerNotMoved) && (p.escapePod.value || p.currentSub != null))
            {
                string sub = p.currentSub != null ? p.currentSub.name : "null";
                bool pod = p.escapePod.value;
                p.OnPlayerPositionCheat();
                MovedOutAgain++;
                DeLog.Warn("DE.Ghost was back in escapePod=" + pod + " currentSub=" + sub + ": OnPlayerPositionCheat again (#" + MovedOutAgain + ")");
            }
            HideRenderers();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void HideRenderers()
        {
            Player p = Player.main;
            if (p == null) return;
            Renderer[] all = p.GetComponentsInChildren<Renderer>(true);
            var keep = new List<Renderer>(all.Length);
            HashSet<Renderer> exempt = Exempt;
            for (int i = 0; i < all.Length; i++)
            {
                Renderer r = all[i];
                if (r == null) continue;
                keep.Add(r);
                if (r.enabled && (exempt == null || !exempt.Contains(r))) { r.enabled = false; RenderersHidden++; }
            }
            _hidden = keep.ToArray();
        }

        public string Describe()
        {
            return "ready=" + Ready + " assert=" + (Asserted ? (AssertOk ? "OK" : "FAIL") : "pending") +
                   " assertDriven=" + (DrivenAsserted ? (DrivenAssertOk ? "OK" : "FAIL") : "pending") + " rule=" + (AtPelvis ? "pelvis" : "camera") +
                   " moves=" + Moves + " renderersHidden=" + RenderersHidden + " statsRefrozen=" + StatsRefrozen + " inputRepushed=" + InputRepushed +
                   " movedOutAgain=" + MovedOutAgain + " shieldReasserted=" + ShieldReasserted;
        }
    }
}
