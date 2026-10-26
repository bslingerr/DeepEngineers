using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;

namespace DeepEngineers.SN
{
    // =================================================================================================================
 // S13 "Character and animation v2", the Subnautica side's pure rules (ROADMAP §14 "### S13"; docs/S13-BRIEF.md;
 // CONTRACT §5.4 "S13 conventions"). Unity-free, compiled into DeepEngineers.SN.Tests; the Unity side is S13Sn.cs and
 // the hooks in S5aHost, CharacterMirror, LightMirror, GhostView, ColorMaskPaint (each commented "S13 hook").
 // =================================================================================================================

 /// <summary>The Subnautica side's S13 seeded failures (dev builds; <c>de.mutant S13.Mx on|off</c>).</summary>
    internal static class S13SnMutants
    {
        /// <summary>S13.M1: the 1.3 s dive window shows the diver's view model again under fpbody=se (the hands leak, A1 red).</summary>
        public const string SplashArms = "S13.M1";
        /// <summary>S13.M2: ground contact cancels the swim source under water too (the S5 rule: A2 red).</summary>
        public const string GroundedCancels = "S13.M2";
        /// <summary>S13.M4: the mirrored spots keep Unity's 0.2 m shadow near plane and Unity's own falloff (A4 red).</summary>
        public const string LightUnity = "S13.M4";
        /// <summary>S13.M6: a held tool, gun or block keeps SE's bones while swimming.</summary>
        public const string HeldSeBones = "S13.M6";
        /// <summary>S13.M7: the placement ghost keeps the flat translucent colour (A7 red).</summary>
        public const string GhostFlat = "S13.M7";
        /// <summary>S13.M8: the swim animation from the input axes (S5a's rule) instead of the look-frame velocity (A5 anim red).</summary>
        public const string AnimInput = "S13.M8";
        public static readonly string[] All = { SplashArms, GroundedCancels, LightUnity, HeldSeBones, GhostFlat, AnimInput };
    }

    /// <summary>The character's animation source rules of S13 (points 2 and 6); CharSourceRules calls them.</summary>
    internal static class S13Source
    {
        /// <summary>
 /// Point 2 ("near the seabed or a hull the swim animation stops and the engineer stands as if in air"): swimming by the
 /// contract (extension version 1, inWater, mode Swim or Surface, not seated or dead), and ground contact cancels it
 /// only at the surface (the eye above the water: wading). Submerged (C1 swim flag Underwater, the eye below 0) the swim
 /// holds whatever the contact: Subnautica's diver never walks under water
 /// Before S13 any
 /// contact (C1 swim flag Grounded: SE's CharacterProxy.Supported, the seabed or a hull's top) switched to SE's bones,
 /// i.e. SE's standing pose. <paramref name="mutantGroundedCancels"/> (S13.M2) restores that rule.
 /// </summary>
        public static bool Swimming(bool swimValid, byte flags, byte mode, bool seated, bool dead, bool mutantGroundedCancels)
        {
            if (!swimValid || seated || dead) return false;
            if ((flags & SwimWireFlags.InWater) == 0) return false;
            if (mode != (byte)SwimMode.Swim && mode != (byte)SwimMode.Surface) return false;
            if ((flags & SwimWireFlags.Grounded) == 0) return true;
            if (mutantGroundedCancels) return false;
            return (flags & SwimWireFlags.Underwater) != 0;
        }

        /// <summary>
 /// Review 7: the C1 locomotion extension (version 1) says the character is on a ladder (state Ladder or the Ladder flag):
 /// SE's ladder pose, never the swim stroke (CharSourceInputs.Ladder joins Seated in <see cref="Swimming"/>).
 /// </summary>
        public static bool OnLadder(byte locoVersion, byte state, byte flags)
        {
            return locoVersion == S7aLoco.Version && (state == S7aConventions.C1LocoState.Ladder || (flags & S7aConventions.C1LocoFlags.Ladder) != 0);
        }

        /// <summary>The frame is "submerged with ground contact" (the A2 row's denominator).</summary>
        public static bool SubmergedGrounded(bool swimValid, byte flags, byte mode, bool seated, bool dead)
        {
            return swimValid && !seated && !dead && (flags & SwimWireFlags.InWater) != 0 && (flags & SwimWireFlags.Underwater) != 0 &&
                   (flags & SwimWireFlags.Grounded) != 0 && (mode == (byte)SwimMode.Swim || mode == (byte)SwimMode.Surface);
        }

        /// <summary>
 /// Point 6 ("swim animations while holding tools, guns and blocks"; today SE's bones): swimming with a held item
 /// = <see cref="CharSource.Blend"/>: the swim cycle's legs (Subnautica's clips, retargeted) under SE's own
 /// upper body (the arms and hands hold the item where SE puts it, so the mirrored item stays in the hands; first person
 /// keeps SE's head as the camera). Without valid SE bones the held case falls back to the full retarget.
 /// <paramref name="mutantHeldSeBones"/> (S13.M6) restores the rule (SE's bones).
 /// </summary>
        public static CharSource Select(bool swimming, bool retargetReady, bool toolHeld, bool bonesValid, bool mutantHeldSeBones)
        {
            if (swimming && retargetReady)
            {
                if (!toolHeld) return CharSource.Retarget;
                if (!mutantHeldSeBones) return bonesValid ? CharSource.Blend : CharSource.Retarget;
            }
            if (bonesValid) return CharSource.SeBones;
            return CharSource.Bind;
        }

        /// <summary>
 /// The Blend mask: true for the bones that take the retarget (the legs: <see cref="S13Conventions.BlendRoots"/> and
 /// every descendant), false for the rest (SE's own bones). Locals are parent-relative, so the legs kick under SE's
 /// own pelvis.
 /// </summary>
        public static bool[] BlendMask(int[] parents, IList<string> names)
        {
            int n = parents != null ? parents.Length : 0;
            var mask = new bool[n];
            if (n == 0 || names == null) return mask;
            var roots = new HashSet<int>();
            for (int i = 0; i < n && i < names.Count; i++)
                foreach (string r in S13Conventions.BlendRoots)
                    if (string.Equals(names[i], r, StringComparison.Ordinal)) roots.Add(i);
            for (int i = 0; i < n; i++)
            {
                int k = i, guard = 0;
                while (k >= 0 && k < n && guard++ <= n)
                {
                    if (roots.Contains(k)) { mask[i] = true; break; }
                    k = parents[k];
                }
            }
            return mask;
        }

        /// <summary>The Blend pose: per bone the retarget's local where the mask is set, SE's local elsewhere (rows past the mask: SE's).</summary>
        public static void ComposeBlend(bool[] mask, Trs[] retarget, Trs[] se, Trs[] target)
        {
            int n = target.Length;
            for (int i = 0; i < n; i++)
                target[i] = mask != null && i < mask.Length && mask[i] && i < retarget.Length ? retarget[i] : se[i];
        }
    }

    /// <summary>
 /// Point 5, Subnautica's side: the swim animation's velocity = the character's velocity in its look frame (the C1 S13
 /// extension), Subnautica's own rule
 /// Before S13 the direction came
 /// from the input axes (S5a's ArmsMotion), so a body moving along the camera while the keys said "back" played the
 /// backward clip going forward.
 /// </summary>
    internal static class S13Anim
    {
        /// <summary>The animator target (x right, y up, z forward, m/s) from the extension; false when it is not usable.</summary>
        public static bool Target(bool valid, ref S13CharWireState s, bool mutantInput, out double x, out double y, out double z)
        {
            x = y = z = 0;
            if (!valid || mutantInput || !s.Has(S13Conventions.C1Flags.Valid)) return false;
            x = s.RightMs;
            y = s.UpMs;
            z = s.ForwardMs;
            return true;
        }

        /// <summary>
 /// The A5 agreement of one sample: the cosine between the animator's horizontal (x, z) and the body-relative velocity
 /// (x, z); NaN below 0.5 m/s (nothing to agree on).
 /// </summary>
        public static double Agreement(double ax, double az, double vx, double vz)
        {
            double la = Math.Sqrt(ax * ax + az * az), lv = Math.Sqrt(vx * vx + vz * vz);
            if (lv < 0.5 || la < 1e-6) return double.NaN;
            return (ax * vx + az * vz) / (la * lv);
        }
    }

    /// <summary>
 /// Point 4 ("shadows become huge in the dark with the flashlight on"): the near-field rules of the mirrored lights. A spot
 /// light's shadow magnifies a caster by surface distance / caster distance; the mirrored suit lamp sits at the helmet
 /// (SE: <c>LightOffset</c> in the head matrix, MyRenderComponentCharacter.UpdateLightPosition) with the helmet,
 /// the arms and the held tool within a metre, all casting from Unity's 0.2 m near plane: the giant shadows. SE never lets
 /// them cast
 /// and clips every spot shadow at 0.5 m.: the near plane was not enough (the
 /// trunk and the legs cast when looking down); the character now lives on a layer its own lamps exclude
 /// (<see cref="S7pSelfShadowRules"/>), the near plane stays for Subnautica's own near casters.
 /// </summary>
    internal static class S13LightRules
    {
        /// <summary>Unity's defaults (the mutant S13.M4 keeps them): near plane 0.2 m.</summary>
        public const float UnityNearPlane = 0.2f;
        /// <summary>Unity's default shadow biases, set explicitly so a live tune is possible (HYPOTHESIS: no change of look).</summary>
        public const float ShadowBias = 0.05f, ShadowNormalBias = 0.4f;

        /// <summary>The shadow near plane of a mirrored light (m); points keep Unity's (they never cast in the mirror, S7 rule).</summary>
        public static float NearPlane(LightKind kind, bool mutantUnity, float characterOverride = float.NaN)
        {
            if (mutantUnity) return UnityNearPlane;
            switch (kind)
            {
                case LightKind.CharacterSpot:
                    return !float.IsNaN(characterOverride) && characterOverride > 0f ? characterOverride : S13Conventions.Light.NearPlaneCharacterSpotMm / 1000f;
                case LightKind.BlockSpot: return S13Conventions.Light.NearPlaneBlockSpotMm / 1000f;
                default: return UnityNearPlane;
            }
        }

        private static readonly Dictionary<int, double> RangeCache = new Dictionary<int, double>();

        /// <summary>
 /// The falloff fit of a kind (the character's lamp by default; <paramref name="blockSpots"/> also fits block spots):
 /// Unity's range × <paramref name="rangeFactor"/> (Wire <see cref="S13LightFit.RangeFactor"/>) and intensity ×
 /// <paramref name="intensityFactor"/> (<see cref="S13LightFit.EnergyFactor"/>); (1, 1) when off or under S13.M4.
 /// The fit is cached per falloff (1/1000 steps).
 /// </summary>
        public static void Fit(LightKind kind, float falloff, bool fitOn, bool blockSpots, bool mutantUnity, out float rangeFactor, out float intensityFactor)
        {
            rangeFactor = intensityFactor = 1f;
            if (!fitOn || mutantUnity) return;
            if (kind != LightKind.CharacterSpot && !(blockSpots && kind == LightKind.BlockSpot)) return;
            if (float.IsNaN(falloff) || falloff < 0f) falloff = 0f;
            int key = (int)Math.Round(falloff * 1000.0);
            double k;
            lock (RangeCache)
                if (!RangeCache.TryGetValue(key, out k)) { k = S13LightFit.RangeFactor(key / 1000.0); RangeCache[key] = k; }
            rangeFactor = (float)k;
            intensityFactor = (float)S13LightFit.EnergyFactor(k);
        }

        /// <summary>The "measured" shadow row: a 0.1 m caster at the near plane, its shadow on the lit surface at <paramref name="surfaceM"/>.</summary>
        public static double WorstShadowLength(float nearPlane, double surfaceM)
        {
            return S13LightFit.ShadowLength(0.1, nearPlane, surfaceM);
        }
    }

    /// <summary>Point 7: the placement ghost's look.</summary>
    internal static class S13GhostRules
    {
        /// <summary>The ghost's alpha.</summary>
        public const float Alpha = 0.45f;
        /// </summary>
        public const float AlphaInside = 0.12f;
        /// <summary>The emission share of the tint (the ghost reads in the dark).</summary>
        public const float Emission = 0.18f;

        public static void Tint(bool allowed, bool inside, out float r, out float g, out float b, out float a)
        {
            GhostRules.Colour(allowed, out r, out g, out b, out a);
            a = inside ? AlphaInside : Alpha;
        }

        /// <summary>Textured (the slot's normal map on the real block mesh) unless switched off, the slot has no map yet, or S13.M7.</summary>
        public static bool Textured(bool on, bool hasNormalMap, bool mutantFlat)
        {
            return on && hasNormalMap && !mutantFlat;
        }

        /// <summary>
 /// Review 4: the render probe's verdict on a ghost material drawn over (0, 0, 0, 0): translucent (alpha below 0.9: the
 /// blend variant ran; a stripped <c>_ALPHABLEND_ON</c> writes alpha 1) and, when <paramref name="requireTint"/> (the glass
 /// Standard's lighting in a
 /// Blit is not the scene's, so its tint is not required.
 /// </summary>
        public static bool ProbeOk(float r, float g, float b, float a, bool requireTint)
        {
            if (float.IsNaN(a) || a < 0f || a >= 0.9f) return false;
            if (!requireTint) return true;
            return g > r + 0.005f && g >= b;
        }

        /// <summary>The camera inside an axis-aligned box grown by <paramref name="marginM"/>.</summary>
        public static bool Inside(float px, float py, float pz, float minX, float minY, float minZ, float maxX, float maxY, float maxZ, float marginM)
        {
            return px >= minX - marginM && px <= maxX + marginM && py >= minY - marginM && py <= maxY + marginM && pz >= minZ - marginM && pz <= maxZ + marginM;
        }
    }

    /// <summary>
 /// Review 1 (point 3): what Subnautica actually received and painted for the held tool (the s13.tooln row), judged on the
 /// receiving side: a colour of hue 0 with a saturation offset ≥ 0 is SE's shader key at saturation ≥ 0.8, i.e. red; a slot
 /// whose texture set has a paintable area and is converted, in bake mode, and still not painted is an unapplied bake.
 /// </summary>
    internal static class S13ToolSlot
    {
        public const string Applied = "applied", Pending = "pending", Converting = "converting", None = "none", Approx = "approx", Foreign = "foreign", NoSlot = "noslot";
        /// <summary>A pending bake older than this fails the A3 row.</summary>
        public const double PendingGraceS = 10.0;

        public static bool Red(float h, float s)
        {
            return !float.IsNaN(h) && !float.IsNaN(s) && (Math.Abs(h) < 1e-4f || Math.Abs(h - 1f) < 1e-4f) && s >= 0f;
        }

        public static string State(bool hasSlot, bool foreign, bool bakeMode, bool hasSet, bool converted, float paintMax, bool applied)
        {
            if (!hasSlot) return NoSlot;
            if (foreign) return Foreign;
            if (applied) return Applied;
            if (!bakeMode) return Approx;
            if (!hasSet || !converted) return Converting;
            if (!(paintMax > 0f)) return None;
            return Pending;
        }
    }

    /// <summary>Point 1: the hands-leak verdict per frame (A1).</summary>
    internal static class S13Arms
    {
        /// <summary>
 /// The diver's view model (Subnautica's own hands and body) drawn while it must not be: under fpbody=se once SE's
 /// astronaut is known (S5 host gate ForceOff), in any camera mode. Before S13 the 1.3 s dive window
 /// showed it at every surface crossing in first person, beside SE's own first-person body.
 /// </summary>
        public static bool Leak(bool armsVisible, bool gateForcedOff)
        {
            return armsVisible && gateForcedOff;
        }
    }

    /// <summary>The s13.char counters (1 Hz row, Subnautica's JSONL).</summary>
    internal sealed class S13CharCounters
    {
        public long Frames, ForceOffFrames, LeakFrames, FirstPersonForceOffFrames;
        /// <summary>Review 3: onPreCull samples (the drawn state), of them under ForceOff, frames whose view-model flag said visible under ForceOff.</summary>
        public long PreCullFrames, PreCullForceOffFrames, LeakVisibleFrames;
        public long SubmergedGroundedFrames, SubmergedGroundedSwim, SubmergedGroundedSourceSwim;
        public long HeldSwimFrames, HeldSwimBlend, HeldSwimSeBones;
        public long ToolFramesNoRow, ToolFramesShownNoRow, ToolFramesWithRow;
        public long AnimFrames, AnimAgreeSamples, AnimAgreeBad, AnimFromExt;
        public double AnimAgreeMin = double.NaN;
        public long Splashes, SplashSounds, SplashesFirstPerson;

        public void NoteAgreement(double c)
        {
            if (double.IsNaN(c)) return;
            AnimAgreeSamples++;
            if (c < 0.7) AnimAgreeBad++;
            if (double.IsNaN(AnimAgreeMin) || c < AnimAgreeMin) AnimAgreeMin = c;
        }

        public string Describe()
        {
            return "frames=" + Frames + " leak=" + LeakFrames + "/" + ForceOffFrames + " groundedSubmerged=" + SubmergedGroundedFrames + " swim=" + SubmergedGroundedSourceSwim +
                   " heldSwim=" + HeldSwimFrames + " blend=" + HeldSwimBlend + " seBones=" + HeldSwimSeBones + " toolNoRow=" + ToolFramesNoRow + " shownNoRow=" + ToolFramesShownNoRow +
                   " anim=" + AnimFromExt + "/" + AnimFrames + " agreeBad=" + AnimAgreeBad + "/" + AnimAgreeSamples +
                   " agreeMin=" + (double.IsNaN(AnimAgreeMin) ? "-" : AnimAgreeMin.ToString("0.###", CultureInfo.InvariantCulture)) + " splashes=" + Splashes + " (1st " + SplashesFirstPerson + ") sounds=" + SplashSounds +
                   " preCull=" + PreCullForceOffFrames + "/" + PreCullFrames + " leakFlag=" + LeakVisibleFrames;
        }
    }
}
