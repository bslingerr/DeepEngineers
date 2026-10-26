using System;
using System.Globalization;
using DeepEngineers.Wire;

namespace DeepEngineers.SN
{
    /// <summary>Where the ghost's translucent material comes from (CONTRACT §5.4 "Placement ghost", Subnautica).</summary>
    internal enum GhostMaterialSource
    {
        /// <summary>The first available of Glass, Standard, Sprite (the contract's chain).</summary>
        Auto = 0,
        /// <summary>A clone of the S5 vanilla glass material (the Seamoth's), tinted through <c>_Color</c>.</summary>
        Glass = 1,
        /// <summary><c>Shader.Find("Standard")</c> in Fade mode.</summary>
        Standard = 2,
        /// <summary><c>Shader.Find("Sprites/Default")</c>.</summary>
        Sprite = 3,
        /// <summary>Nothing available yet (the ghost renderers stay hidden).</summary>
        None = 4,
    }

    /// <summary>
 /// The Unity-free rules of the Subnautica ghost view (<see cref="GhostView"/>; CONTRACT §5.4 "S7a conventions",
 /// "Placement ghost"): marker recognition (Wire's <see cref="S7aGhost"/>), the two colours, the material chain,
 /// visibility, and the presented-tick bookkeeping of the s7a.ghost rows. Compiled into tests/DeepEngineers.SN.Tests.
 /// </summary>
    internal static class GhostRules
    {
        /// <summary>Allowed: green (0.25, 1.0, 0.35, 0.40).</summary>
        public const float GreenR = 0.25f, GreenG = 1.0f, GreenB = 0.35f, GreenA = 0.40f;
        /// <summary>Blocked: red (1.0, 0.25, 0.20, 0.40).</summary>
        public const float RedR = 1.0f, RedG = 0.25f, RedB = 0.20f, RedA = 0.40f;
        /// <summary>The transparent render queue (Unity's Transparent = 3000).</summary>
        public const int TransparentQueue = 3000;
        /// <summary>s7a.ghost rows at most every 100 ms while present (10 Hz), and on every change.</summary>
        public const ulong PresentRowMs = 100;

        /// <summary>The placement ghost: the id, or a version-1 GridAdd with the Ghost bit (S6 reads the same marker).</summary>
        public static bool IsGhost(ulong id, uint gridFlags) { return S7aGhost.IsGhostGrid(id, gridFlags); }

        /// <summary>The colour of the newest C2 row: green while it has ghostAllowed, red otherwise.</summary>
        public static void Colour(bool allowed, out float r, out float g, out float b, out float a)
        {
            if (allowed) { r = GreenR; g = GreenG; b = GreenB; a = GreenA; }
            else { r = RedR; g = RedG; b = RedB; a = RedA; }
        }

        /// <summary>A ghost renderer is enabled only while SE shows the gizmo cube, it has a mesh, and the S5 cull keeps it.</summary>
        public static bool Visible(bool shown, bool hasMesh, bool culled) { return shown && hasMesh && !culled; }

        /// <summary><c>auto|glass|standard|sprite</c> (case-insensitive); false for anything else.</summary>
        public static bool TryParseSource(string s, out GhostMaterialSource src)
        {
            src = GhostMaterialSource.Auto;
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "auto": src = GhostMaterialSource.Auto; return true;
                case "glass": src = GhostMaterialSource.Glass; return true;
                case "standard": src = GhostMaterialSource.Standard; return true;
                case "sprite": src = GhostMaterialSource.Sprite; return true;
                default: return false;
            }
        }

        public static string Name(GhostMaterialSource s)
        {
            switch (s)
            {
                case GhostMaterialSource.Auto: return "auto";
                case GhostMaterialSource.Glass: return "glass";
                case GhostMaterialSource.Standard: return "standard";
                case GhostMaterialSource.Sprite: return "sprite";
                default: return "none";
            }
        }

        /// <summary>
 /// The material source to build: an explicit request when that source is available, else the contract's chain
 /// (first available and supported: glass, standard, sprite); <see cref="GhostMaterialSource.None"/> when nothing is.
 /// </summary>
        public static GhostMaterialSource Choose(GhostMaterialSource requested, bool glassOk, bool standardOk, bool spriteOk)
        {
            switch (requested)
            {
                case GhostMaterialSource.Glass: if (glassOk) return requested; break;
                case GhostMaterialSource.Standard: if (standardOk) return requested; break;
                case GhostMaterialSource.Sprite: if (spriteOk) return requested; break;
            }

            // (_Color is not its tint); Unity's Standard shader in Fade mode shows the colour, so Auto prefers it
            if (standardOk) return GhostMaterialSource.Standard;
            if (glassOk) return GhostMaterialSource.Glass;
            if (spriteOk) return GhostMaterialSource.Sprite;
            return GhostMaterialSource.None;
        }

        /// <summary>
 /// Auto mode upgrades once to the glass clone when it becomes available later (the Seamoth prefab is read
 /// asynchronously after the world is up); an explicit choice never changes by itself.
 /// </summary>
        public static bool ShouldRebuild(GhostMaterialSource requested, GhostMaterialSource current, bool glassOk, bool standardOk, bool spriteOk)
        {
            GhostMaterialSource want = Choose(requested, glassOk, standardOk, spriteOk);
            return want != current;
        }

        /// <summary>Two C2 poses differ (bitwise on the float32 wire values, as SE wrote them).</summary>
        public static bool PoseDiffers(double ox, double oy, double oz, double qx, double qy, double qz, double qw,
                                       double px, double py, double pz, double rx, double ry, double rz, double rw)
        {
            return (float)ox != (float)px || (float)oy != (float)py || (float)oz != (float)pz ||
                   (float)qx != (float)rx || (float)qy != (float)ry || (float)qz != (float)rz || (float)qw != (float)rw;
        }

        /// <summary>|a − b| in millimetres.</summary>
        public static double ErrMm(double ax, double ay, double az, double bx, double by, double bz)
        {
            double dx = ax - bx, dy = ay - by, dz = az - bz;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz) * 1000.0;
        }

        public static string Hex(ulong v) { return "0x" + v.ToString("x16", CultureInfo.InvariantCulture); }
    }

    /// <summary>
 /// The first presented frame of each ghost pose change (s7a.ghost <c>firstPresentedTick</c>, S7a.N3 "latency"): the C2
 /// tick of a snapshot whose ghost pose differs from the previous snapshot's is pending until a camera sample presents a
 /// bracket whose newer tick is at or past it; that sample's newer tick and newest C1 tick are reported. A newer change
 /// replaces an unpresented older one (the older pose was never drawn on its own).
 /// </summary>
    internal sealed class GhostPresentTracker
    {
        private ulong _lastRowTick;
        private bool _hasLast;
        private double _ox, _oy, _oz, _qx, _qy, _qz, _qw;

        /// <summary>The pending change's C2 tick (0 = none).</summary>
        public ulong PendingTick { get; private set; }
        public ulong LastChangeTick { get; private set; }
        public ulong FirstPresentedTick { get; private set; }
        /// <summary>The newest C1 tick at the presenting sample: SE's clock when Subnautica drew the change.</summary>
        public ulong NewestAtPresent { get; private set; }
        public long Changes, Presented, Superseded;

        /// <summary>The ghost's row of the newest C2 snapshot (tick and pose); a pose change becomes pending.</summary>
        public bool NoteRow(ulong c2Tick, double ox, double oy, double oz, double qx, double qy, double qz, double qw)
        {
            if (_hasLast && c2Tick == _lastRowTick) return false;
            bool changed = !_hasLast || GhostRules.PoseDiffers(ox, oy, oz, qx, qy, qz, qw, _ox, _oy, _oz, _qx, _qy, _qz, _qw);
            _hasLast = true;
            _lastRowTick = c2Tick;
            _ox = ox; _oy = oy; _oz = oz; _qx = qx; _qy = qy; _qz = qz; _qw = qw;
            if (!changed) return false;
            if (PendingTick != 0) Superseded++;
            PendingTick = c2Tick;
            LastChangeTick = c2Tick;
            Changes++;
            return true;
        }

        /// <summary>A camera sample presented <paramref name="presentedTick"/> (the bracket's newer tick): true when it resolves the pending change.</summary>
        public bool OnPresented(ulong presentedTick, ulong newestTick)
        {
            if (PendingTick == 0 || presentedTick == 0 || presentedTick < PendingTick) return false;
            FirstPresentedTick = presentedTick;
            NewestAtPresent = newestTick;
            PendingTick = 0;
            Presented++;
            return true;
        }

        /// <summary>The ghost left (EntityRemove, ClearAll): nothing pending, the next row is a change.</summary>
        public void Reset()
        {
            _hasLast = false;
            PendingTick = 0;
        }
    }

    /// <summary>What one Subnautica s7a.ghost row says (for the change test).</summary>
    internal struct GhostViewState
    {
        public bool Present, Shown, Allowed;
        public string Material;
        public int Renderers;

        public bool SameAs(GhostViewState o)
        {
            return Present == o.Present && Shown == o.Shown && Allowed == o.Allowed && Renderers == o.Renderers &&
                   string.Equals(Material, o.Material, StringComparison.Ordinal);
        }
    }
}
