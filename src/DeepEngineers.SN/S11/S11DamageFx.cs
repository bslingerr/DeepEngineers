using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// "): under Subnautica's
 /// picture the diver's damage is shown with Subnautica's own damage FX instead of SE's red overlay. VERIFIED
 /// <list type="bullet">
 /// <item>The game's path: <c>Player.OnTakeDamage(DamageInfo)</c> calls <c>DamageFX.main.AddHudDamage(damage / 100,
 /// position, info)</c> for any damage but Radiation. The ghost Player is shielded (damage 0), so it
 /// never fires for the SE character: S11 calls it itself.</item>
 /// <item><c>public static DamageFX DamageFX.main</c>; <c>public void AddHudDamage(float, Vector3,
 /// DamageInfo)</c> = <c>CreateImpactEffect</c>
 /// and
 /// <c>PlayScreenFX</c>
 /// <c>public void ClearHudDamage</c>. <c>DamageInfo</c>'s public fields.</item>
 /// <item>The splatters are uGUI under the HUD, which the bridge's MaskHUD hides (HideForScreenshots → uGUI.SetVisible(false),
 /// see S16SnHud): each new one is moved under S16's overlay canvas <c>DE.S16.Hud</c> (shared, found by name; S11 makes no
 /// canvas of its own). The camera image effects need no canvas.</item>
 /// </list>
 /// At most one call every 250 ms per damage type (<c>format fx.sn</c>); leaving Subnautica's picture clears what is
 /// showing (an image effect only fades while its camera renders). The C7 S11 block tells SE whether these FX exist
 /// (DamageFxOk) so SE drops its own overlay under the picture (S11Hud, <c>format hud.blood</c>); without them SE keeps it.
 /// Main thread; never throws.
 /// </summary>
    internal static class S11DamageFx
    {
        public const float MinIntervalS = 0.25f;
        private const string CanvasName = "DE.S16.Hud";
        private static readonly float[] _lastByType = new float[32];
        private static bool _ok, _missingLogged, _wasPicture;
        private static float _nextCheckS, _lastShownS = -100f;
        private static int _screenFx;
        private static Transform _canvas;
        private static Canvas _canvasComp;
        public static long Played, Moved, Cleared, NotPicture, Missing, RateLimited;
        public static string LastPlayed = "";

        public static bool Ok { get { return _ok; } }

        /// <summary>Subnautica's picture is on (the C5 view state, S7aSnState: Underwater or Surfacing).</summary>
        public static bool Picture { get { return S7aSnState.C5Valid && (S7aSnState.C5View == ViewState.Underwater || S7aSnState.C5View == ViewState.Surfacing); } }

        /// <summary>The C7 S11 flags (<c>format c7.s11</c>).</summary>
        public static byte Flags()
        {
            int f = (_ok ? S11Conventions.C7Flags.DamageFxOk : 0) | (Time.realtimeSinceStartup - _lastShownS < S11Conventions.Hud.HitFadeMs / 1000f ? S11Conventions.C7Flags.DamageFxShown : 0);
            return (byte)f;
        }

        /// <summary>From S11Host.Tick: the FX found (every 2 s until they are), cleared when the picture goes.</summary>
        public static void Frame(float nowS)
        {
            try
            {
                if (nowS >= _nextCheckS)
                {
                    _nextCheckS = nowS + 2f;
                    DamageFX fx = DamageFX.main;
                    SNCameraRoot root = SNCameraRoot.main;
                    _screenFx = root != null ? root.gameObject.GetComponentsInChildren<DamageScreenFX>(true).Length : 0;
                    bool ok = fx != null && _screenFx > 0;
                    if (ok != _ok)
                        DeLog.Info("DE.S11 Subnautica damage FX " + (ok ? "found: DamageFX.main and " + _screenFx + " DamageScreenFX (the diver's damage shows Subnautica's effects under its picture)" : "lost"));
                    if (!ok && !_missingLogged && fx == null && root != null)
                    {
                        _missingLogged = true;
                        DeLog.Warn("DE.S11 MISSING DamageFX.main (SE keeps its own damage overlay)");
                    }
                    _ok = ok;
                    if (_canvas == null)
                    {
                        GameObject go = GameObject.Find(CanvasName);
                        if (go != null) { _canvas = go.transform; _canvasComp = go.GetComponent<Canvas>(); }
                    }
                }
                bool picture = Picture;
                if (_wasPicture && !picture && _ok)
                {
                    DamageFX.main.ClearHudDamage();
                    Cleared++;
                }
                _wasPicture = picture;
            }
            catch (Exception) { }
        }

        /// <summary>A hazard record or a bite on the SE character: Subnautica's own damage FX for <paramref name="type"/>.</summary>
        public static void Play(int type, float snHp, Vector3 at)
        {
            try
            {
                if (!(snHp > 0f) || type == (int)DamageType.Radiation) return;
                if (!Picture) { NotPicture++; return; }
                if (!_ok) { Missing++; return; }
                float now = Time.realtimeSinceStartup;
                int k = type & 31;
                if (now - _lastByType[k] < MinIntervalS) { RateLimited++; return; }
                _lastByType[k] = now;
                DamageFX fx = DamageFX.main;
                var info = new DamageInfo { originalDamage = snHp, damage = snHp, position = at, type = (DamageType)type, dealer = null };
                int before = fx.transform.childCount;
                fx.AddHudDamage(snHp / 100f, at, info);
                int moved = MoveSplatters(fx.transform, before);
                Played++;
                _lastShownS = now;
                LastPlayed = ((DamageType)type).ToString() + " " + snHp.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                Row(type, snHp, moved);
            }
            catch (Exception e) { DeLog.WarnOnce("s11.dmgfx", "DE.S11 damage FX failed: " + e.GetType().Name + ": " + e.Message); }
        }

        private static readonly List<Transform> _scratch = new List<Transform>();

        /// <summary>The splatters CreateImpactEffect just spawned (DamageFX's new children) go under S16's canvas.</summary>
        private static int MoveSplatters(Transform parent, int before)
        {
            if (_canvas == null) return 0;
            _scratch.Clear();
            for (int i = before; i < parent.childCount; i++) _scratch.Add(parent.GetChild(i));
            int n = 0;
            foreach (Transform t in _scratch)
            {
                if (t == null || t.GetComponent<Damage>() == null) continue;
                t.SetParent(_canvas, false);
                n++;
            }
            Moved += n;
            return n;
        }

        private static void Row(int type, float hp, int moved)
        {
            S11Host h = S11Hooks.Host;
            if (h != null && !h.RowAllowedPublic()) return;
            JsonOut j = S5Rows.Begin("s11.dmgfx");
            j.Str("type", ((DamageType)type).ToString()).Num("hp", (double)hp).Num("splatters", (long)moved).Bool("canvas", _canvas != null)
             .Bool("canvasShown", _canvasComp != null && _canvasComp.enabled).Num("screenFx", (long)_screenFx).Num("played", Played);
            S5Rows.End(j);
        }

        public static void Teardown()
        {
            try { if (_ok && DamageFX.main != null) DamageFX.main.ClearHudDamage(); } catch (Exception) { }
            _ok = false;
            _canvas = null;
            _canvasComp = null;
            _wasPicture = false;
            Array.Clear(_lastByType, 0, _lastByType.Length);
        }

        public static string Describe()
        {
            return (_ok ? "ok" : "missing") + ", played " + Played + ", splatters moved " + Moved + ", not under the picture " + NotPicture + ", rate-limited " + RateLimited + ", cleared " + Cleared;
        }
    }
}
