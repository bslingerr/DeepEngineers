using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace DeepEngineers.SN
{
    /// <summary>One Unity material: an SE material (MaterialDef) in one paint colour, with its texture set.</summary>
    internal sealed class MaterialSlot
    {
        public ulong MaterialKey;
        public long ColourKey;
        public float H, S, V;
        public Material Mat;
        public TextureSet Set;
        public int AppliedVersion = -1;
        public string PlannedKeywords = "";
        public string Label;
        /// <summary>S7t-colour-mask (T6): the block paint's state of this slot (null: not a block slot in use, e.g. the character's).</summary>
        public ColorMaskEntry Mask;
    }

    /// <summary>
 /// MarmosetUBER for the mirror (ARCHITECTURE §5.8 "Shader", "Texture slots", "Paint"; probe-ship-render §2.3, §3.1):
 /// <list type="bullet">
 /// <item>The shader comes from Addressables, key <c>Assets/Marmoset/Shader/Uber/MarmosetUber.shader</c>, requested
 /// only once the host world is up (a fallback shader in the menu was Killcraft's gotcha) through Subnautica's own
 /// <c>AddressablesUtility.LoadAsync&lt;Shader&gt;</c>; never <c>Shader.Find</c> (two MarmosetUBER copies exist). The
 /// handle is released with <c>AddressablesUtility.QueueRelease</c> when the world goes. If Addressables fails, the
 /// reference Seamoth's hull shader is adopted when its name is MarmosetUBER (the ValCraft pattern), logged.</item>
 /// <item>A material per (SE material × colour): Seamoth-like scalars (_SpecInt 1, _Shininess 7, _Fresnel 0),
 /// _ZWRITE_ON; maps as they arrive: _MainTex (raw BC7 _cm), _SpecTex + MARMO_SPECMAP, _BumpMap, _MultiColorMask +
 /// UWE_3COLOR with _Color/_SpecColor set the way ColorCustomizer.SetColors does, _Illum + MARMO_EMISSION only when
 /// the emissive mask is non-zero. Instancing off (the SkyApplier gives every renderer its own property block).</item>
 /// <item>S7t-colour-mask (T6): the tint is computed on SE's shader key (ColorMaskHSV + ColorizeHSV,
 /// <see cref="ColorMaskRules.ShaderKey"/>), and a block slot in use gets SE's colour per texel from
 /// <see cref="ColorMask"/> after every ApplyMaps (UWE_3COLOR off then).</item>
 /// </list>
 /// A missing member or a failed load disables this feature only (logged once); the renderers then stay hidden.
 /// </summary>
    internal sealed class MarmosetMaterials
    {
        public const string ShaderKey = "Assets/Marmoset/Shader/Uber/MarmosetUber.shader";
        public const string ExpectedName = "MarmosetUBER";
        public const ulong LoadTimeoutMs = 30000;

        private AsyncOperationHandle<Shader> _handle;
        private bool _requested;
        private ulong _requestMs;
        private bool _timeoutLogged;

        public Shader Shader { get; private set; }
        public bool Failed { get; private set; }
        public string Source { get; private set; }
        public string Error { get; private set; }
        public long MaterialsCreated;
        /// <summary>Addressables failures so far (each one lets the lab try the reference Seamoth's shader once more).</summary>
        public long Failures { get; private set; }

        public bool Ready { get { return Shader != null; } }

        /// <summary>S7t-colour-mask (T6): SE's colour per texel on the block slots (set by MirrorConsumer; null in tests).</summary>
        public ColorMaskPaint ColorMask;

        public string State
        {
            get
            {
                if (Shader != null) return "ready (" + Source + ", name=" + Shader.name + ", supported=" + Shader.isSupported + ")";
                if (Failed) return "FAILED: " + Error;
                return _requested ? "loading" : "not requested (world not up)";
            }
        }

        /// <summary>Main thread, every frame while the host world is up: request once, then poll the handle.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Tick(ulong nowMs)
        {
            if (Shader != null || Failed) return;
            if (!_requested)
            {
                _requested = true;
                _requestMs = nowMs;
                _handle = AddressablesUtility.LoadAsync<Shader>(ShaderKey);
                DeLog.Info("DE.Mirror shader request Addressables '" + ShaderKey + "' (AddressablesUtility.LoadAsync<Shader>; never Shader.Find)");
                return;
            }
            if (!_handle.IsValid()) { Fail("the Addressables handle is not valid"); return; }
            if (!_handle.IsDone)
            {
                if (!_timeoutLogged && nowMs - _requestMs > LoadTimeoutMs)
                {
                    _timeoutLogged = true;
                    DeLog.Warn("DE.Mirror shader still loading after " + LoadTimeoutMs + " ms (key '" + ShaderKey + "')");
                }
                return;
            }
            if (_handle.Status == AsyncOperationStatus.Succeeded && _handle.Result != null)
            {
                Shader = _handle.Result;
                Source = "Addressables";
                DeLog.Info("DE.Mirror shader '" + Shader.name + "' supported=" + Shader.isSupported + " renderQueue=" + Shader.renderQueue +
                           " after " + (nowMs - _requestMs) + " ms (" + (Shader.name == ExpectedName ? "S3.C4 name ok" : "UNEXPECTED NAME, expected " + ExpectedName) + ")");
                return;
            }
            Exception ex = _handle.OperationException;
            Fail("status " + _handle.Status + (ex != null ? " " + ex.GetType().Name + ": " + ex.Message : ""));
        }

        private void Fail(string why)
        {
            Failed = true;
            Failures++;
            Error = why;
            DeLog.Error("DE.Mirror FEATURE marmoset DISABLED: the MarmosetUBER shader did not load from Addressables (" + why + "); mirror renderers stay hidden " +
                        "unless the reference Seamoth's shader can be adopted");
        }

        /// <summary>Fallback when Addressables failed: a vanilla renderer's MarmosetUBER shader (the reference Seamoth's hull).</summary>
        public bool TryAdopt(Shader s, string from)
        {
            if (Shader != null || s == null || s.name != ExpectedName) return false;
            Shader = s;
            Source = "adopted from " + from;
            Failed = false;
            DeLog.Warn("DE.Mirror shader adopted from " + from + " (Addressables failed: " + Error + ") supported=" + s.isSupported);
            return true;
        }

        /// <summary>The world went away: release the Addressables handle (the shader may be unloaded); request again next world.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Release()
        {
            try { if (_requested && _handle.IsValid()) AddressablesUtility.QueueRelease(ref _handle); } catch (Exception) { }
            _requested = false;
            _timeoutLogged = false;
            Shader = null;
            Failed = false;
            Error = null;
            Source = null;
        }

        /// <summary>A new material for an SE material in one colour (maps applied later by <see cref="ApplyMaps"/>).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public Material Create(MaterialSlot slot, MirrorObjects owned)
        {
            var m = owned.Track(new Material(Shader) { name = "DE.Mirror " + slot.Label });
            MaterialsCreated++;
            m.enableInstancing = false;
            SetFloat(m, "_SpecInt", 1f);
            SetFloat(m, "_Shininess", 7f);
            SetFloat(m, "_Fresnel", 0f);
            m.EnableKeyword("_ZWRITE_ON");
            SetColour(m, "_Color", Color.white);
            SetColour(m, "_SpecColor", Color.white);
            return m;
        }

        private static void SetFloat(Material m, string name, float v) { if (m.HasProperty(name)) m.SetFloat(name, v); }
        private static void SetColour(Material m, string name, Color c) { if (m.HasProperty(name)) m.SetColor(name, c); }
        private static void SetTex(Material m, string name, Texture t) { if (t != null && m.HasProperty(name)) m.SetTexture(name, t); }

        /// <summary>
 /// Puts the texture set's ready maps and their keywords on the material (called again whenever the set's version
 /// changes). The paint colour applies only with the mask (UWE_3COLOR): before it, _Color stays white.
 /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ApplyMaps(MaterialSlot slot)
        {
            Material m = slot.Mat;
            TextureSet t = slot.Set;
            if (m == null) return;
            var planned = new List<string> { "_ZWRITE_ON" };
            if (t != null)
            {
                SetTex(m, "_MainTex", t.Main);
                if (t.Spec != null) { SetTex(m, "_SpecTex", t.Spec); m.EnableKeyword("MARMO_SPECMAP"); }
                if (t.Bump != null) SetTex(m, "_BumpMap", t.Bump);
                if (t.Mask != null)
                {
                    SetTex(m, "_MultiColorMask", t.Mask);
                    m.EnableKeyword("UWE_3COLOR");
                    float kh, ks, kv;
                    ColorMaskRules.ShaderKey(slot.H, slot.S, slot.V, out kh, out ks, out kv); // S7t-colour-mask: SE adds ColorizeHSV (0, 0.8, -0.1) to every key
                    Rgba c = PaintRules.MaterialColour(kh, ks, kv);
                    Rgba sc = PaintRules.SpecularFor(c);
                    SetColour(m, "_Color", new Color(c.R, c.G, c.B, c.A));
                    SetColour(m, "_SpecColor", new Color(sc.R, sc.G, sc.B, sc.A));
                    SetColour(m, "_Color2", Color.white);
                    SetColour(m, "_SpecColor2", Color.white);
                    SetColour(m, "_Color3", Color.white);
                    SetColour(m, "_SpecColor3", Color.white);
                }
                if (t.Illum != null && t.EmissiveMax > 2f / 255f) { SetTex(m, "_Illum", t.Illum); m.EnableKeyword("MARMO_EMISSION"); }
                else m.DisableKeyword("MARMO_EMISSION");
                if (t.ConvertState == TextureSet.Conv.Ready || t.ConvertState == TextureSet.Conv.Off)
                {
                    if (t.Spec != null) planned.Add("MARMO_SPECMAP");
                    if (t.Mask != null) planned.Add("UWE_3COLOR");
                    if (t.Illum != null && t.EmissiveMax > 2f / 255f) planned.Add("MARMO_EMISSION");
                }
                else
                {
                    planned.Add("MARMO_SPECMAP");
                    planned.Add("UWE_3COLOR");
                }
                slot.AppliedVersion = t.Version;
            }
            planned.Sort(StringComparer.Ordinal);
            slot.PlannedKeywords = string.Join(" ", planned.ToArray());
            // S7t-colour-mask (T6): a block slot carrying SE's colour per texel gets it back over the S3 maps just set
            if (ColorMask != null && slot.Mask != null)
            {
                try { ColorMask.AfterApplyMaps(slot); }
                catch (Exception e) { ColorMask.OnHookException("ApplyMaps", e); }
            }
        }

        /// <summary>S3.C4 description: shader name, support, keywords on the material (sorted).</summary>
        public static string Describe(Material m)
        {
            if (m == null) return "material=none";
            Shader s = m.shader;
            string[] kw = m.shaderKeywords ?? new string[0];
            var list = new List<string>(kw);
            list.Sort(StringComparer.Ordinal);
            return "shader=" + (s != null ? s.name : "null") + " supported=" + (s != null && s.isSupported) + " keywords=[" + string.Join(" ", list.ToArray()) + "]" +
                   " renderQueue=" + m.renderQueue;
        }
    }
}
