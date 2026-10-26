using System;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;
using Sandbox.Game.Components;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;

namespace DeepEngineers.SE
{
    /// <summary>
 /// the
 /// coordinator: "can we use Subnautica's effects?"): SE's full-screen blood overlay, set from the last health drop.
 /// <para>What paints the red, VERIFIED <c>MyGuiControlBloodOverlay.Draw</c> draws <c>Textures\Gui\Blood.dds</c>
 /// over the whole screen with the alpha of <c>public float MyRenderComponentCharacter.GetHUDBloodAlpha</c>
 /// That alpha is 0.8 after ANY health drop,
 /// however small
 /// decays only inside the character render
 /// component's <c>Draw</c>, after its early return without a helmet light, and stays 0.3-1.3 while
 /// health is at or under <c>HEALTH_RATIO_CRITICAL</c> 0.2.
 /// 2 HP every second for minutes (hot water at 645 m)
 /// each one a full 0.8 flash, then health under 20 %: red all the time with no damage he could see.</para>
 /// <para>The postfix (Harmony <c>deepengineers.se.s11.hud</c>, while S11 is on) changes only the per-hit pulse: it is
 /// <see cref="S11HudRules.BloodAlpha"/> of the last drop (<c>format hud.blood</c>: a 0.2 HP tick is a faint 0.2 that is
 /// gone 800 ms later, a bite a full 0.8), 0 under Subnautica's picture when its damage FX are there
 /// (<see cref="S11HudRules.SnPaints"/>: the C7 S11 block's DamageFxOk; Subnautica plays its own DamageFX then). The

 /// (<see cref="S11HudRules.Combine"/>), and the game's own value is left while the character is dead. A missing member logs
 /// <c>DE.S11 MISSING MyRenderComponentCharacter.GetHUDBloodAlpha</c> once; SE's own overlay stays then.</para>
 /// </summary>
    internal static class S11Hud
    {
        private const string Id = "deepengineers.se.s11.hud";
        private static bool _tried;
        public static bool Patched { get; private set; }

        private static long _charId;
        private static float _lastHp = float.NaN;
        private static ulong _lastDropMs;
        private static double _lastDropHp;
        private static string _mode = "";
        private static ulong _nextRowMs;
        public static long Drops, SnPainted, SeDrawn, GameKept, Rows;

        public static void Ensure()
        {
            if (_tried) return;
            _tried = true;
            try
            {
                System.Reflection.MethodInfo m = HarmonyLib.AccessTools.Method(typeof(MyRenderComponentCharacter), "GetHUDBloodAlpha", Type.EmptyTypes);
                if (m == null || m.DeclaringType != typeof(MyRenderComponentCharacter) || m.ReturnType != typeof(float))
                {
                    DeLog.Info("DE.S11 MISSING MyRenderComponentCharacter.GetHUDBloodAlpha() (SE's own blood overlay stays)");
                    return;
                }
                new HarmonyLib.Harmony(Id).Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(S11Hud).GetMethod(nameof(Postfix),
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
                Patched = true;
                DeLog.Info("DE.Harmony SE s11 hud: MyRenderComponentCharacter.GetHUDBloodAlpha postfix (the blood overlay fades " +
                           S11Conventions.Hud.HitFadeMs + " ms after the last health drop; Subnautica's damage FX under its picture)");
            }
            catch (Exception e)
            {
                DeLog.Info("DE.S11 MISSING MyRenderComponentCharacter.GetHUDBloodAlpha(): " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>From <see cref="S11Consumer.Tick"/> (update thread, every tick): the local character's health drops.</summary>
        public static void Tick(ulong nowMs)
        {
            try
            {
                MySession s = MySession.Static;
                MyCharacter c = s != null ? s.LocalCharacter : null;
                var h = c != null && c.StatComp != null ? c.StatComp.Health : null;
                if (h == null) { _charId = 0; _lastHp = float.NaN; return; }
                float hp = h.Value;
                if (c.EntityId != _charId || float.IsNaN(_lastHp)) { _charId = c.EntityId; _lastHp = hp; return; }
                if (hp < _lastHp - 1e-4f)
                {
                    _lastDropMs = nowMs;
                    _lastDropHp = _lastHp - hp;
                    Drops++;
                }
                _lastHp = hp;
            }
            catch (Exception) { }
        }

        /// <summary>Subnautica's picture is on and a fresh C7 S11 block says its damage FX can play.</summary>
        public static bool SnPaints(out int flags)
        {
            flags = -1;
            Func<uint> view = PicturePolicy.CurrentViewState;
            uint v = view != null ? view() : ViewState.Space;
            bool picture = v == ViewState.Underwater || v == ViewState.Surfacing;
            Plugin pl = Plugin.Instance;
            Link l = pl != null ? pl.LinkForS1 : null;
            bool fresh = false;
            if (l != null && l.State == LinkState.Up && l.HasSnStatus)
            {
                SnStatus st = l.LastSnStatus;
                byte f;
                fresh = S11Wire.TryReadStatus(ref st, out f);
                if (fresh) flags = f;
            }
            return S11HudRules.SnPaints(picture, fresh, flags < 0 ? 0 : flags);
        }

        private static void Postfix(MyRenderComponentCharacter __instance, ref float __result)
        {
            try
            {
                if (!S11Consumer.Enabled || __instance == null) return;
                var c = __instance.Entity as MyCharacter;
                if (c == null) return;
                ulong now = Wire.Native.Win32.TickCount64();
                float game = __result;
                string mode;
                int flags;
                if (c.IsDead) { mode = "dead"; GameKept++; }
                else
                {
                    // the game's own
 // low-health floor
 // stays, under Subnautica's picture too
                    bool sn = SnPaints(out flags);
                    double ratio = c.StatComp != null ? c.StatComp.HealthRatio : double.NaN;
                    double floor = S11HudRules.LowHealthFloor(ratio, MyCharacterStatComponent.HEALTH_RATIO_CRITICAL);
                    double pulse = _lastDropMs == 0 ? 0.0 : S11HudRules.BloodAlpha(now - _lastDropMs, _lastDropHp);
                    __result = (float)S11HudRules.Combine(pulse, floor, sn);
                    mode = sn ? (floor > 0 ? "sn+low" : "sn") : (floor > 0 ? "se+low" : "se");
                    if (sn) SnPainted++; else SeDrawn++;
                }
                if (mode != _mode || (now >= _nextRowMs && __result > 0f)) Row(mode, game, __result, now);
            }
            catch (Exception) { }
        }

        private static void Row(string mode, float game, float alpha, ulong now)
        {
            _mode = mode;
            _nextRowMs = now + 1000;
            Rows++;
            var sb = new StringBuilder(256);
            sb.Append("{\"t\":"); JsonText.AppendString(sb, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            sb.Append(",\"ev\":\"s11.hud\"");
            S5Rows.Str(sb, "mode", mode);
            S5Rows.Num(sb, "alpha", alpha);
            S5Rows.Num(sb, "game", game);
            S5Rows.Num(sb, "dropHp", _lastDropHp);
            S5Rows.Num(sb, "dtMs", _lastDropMs == 0 ? -1.0 : (double)(now - _lastDropMs));
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
        }

        public static string Describe()
        {
            return (Patched ? "on" : "off") + ", drops " + Drops + ", sn " + SnPainted + ", se " + SeDrawn + ", dead " + GameKept;
        }
    }
}
