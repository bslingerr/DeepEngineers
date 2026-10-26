using System;
using System.Linq.Expressions;
using System.Reflection;

namespace DeepEngineers.SE
{
    /// <summary>
 /// SE render-thread glue of S2 (game-dependent): the bodies of the render hooks of <see cref="S2Patches"/>, and the
 /// <see cref="IRenderGpu"/> that reaches SE's D3D11 device, immediate context and backbuffer through
 /// <c>VRageRender.MyRender11</c> internals, all VERIFIED
 /// <list type="bullet">
 /// <item><c>m_drawScene</c> (<c>private static bool</c>): set true by the <c>DrawScene</c> render message inside
 /// <c>ProcessMessageQueue</c> after <c>FullDraw</c> has reset it; <c>FullDraw</c> then calls
 /// <c>DrawScene</c> only if <c>m_drawScene ∧ SceneSystemsReady</c>, else <c>RC.ClearRtv(Backbuffer, default)</c> =
 /// (0,0,0,0), and <c>ConsumeMainSprites</c> draws the HUD/GUI on top.</item>
 /// <item><c>DeviceInstance</c> (<c>internal static SharpDX.Direct3D11.Device1</c>), <c>Backbuffer</c> (
 /// <c>internal static MyBackbuffer</c>) with <c>Rtv</c> and <c>Resource</c>
 /// SharpDX objects expose
 /// <c>NativePointer</c>. No reference is kept beyond a frame (SE's <c>ResizeBuffers</c> needs the backbuffer free).</item>
 /// </list>
 /// Every body catches everything; a failure is counted and logged through the render log queue.
 /// <para>
 /// S7t T1 (the crossfade, CONTRACT §5.4 "S7t crossfade"): a prefix on <c>MyRender11.ConsumeMainSprites</c>
 /// runs <see cref="CrossfadePass"/> on SE's drawn scene, so the HUD
 /// and the GUI (and S7a's <see cref="CrossingFlash"/>, a GUI sprite) draw over it untouched
 /// The backbuffer's format is
 /// read from its texture (<c>MyBackbuffer</c> creates its RTV without a description, VERIFIED
 /// the RTV format is the swap chain's).
 /// </para>
 /// </summary>
    internal sealed class RenderHooks : IRenderGpu, ICrossfadeGpu
    {
        public static RenderDisplay Display;
        public static RenderHooks Gpu;

        // resolved by S2Patches
        public static FieldInfo DrawSceneField;
        public static PropertyInfo DeviceProperty, BackbufferProperty;
        private static Func<bool> _getDrawScene;
        private static Action _clearDrawScene;
        /// <summary>S7t: the <c>ConsumeMainSprites</c> prefix is installed.</summary>
        public static bool SpritesConsumeOk;

        private readonly AlphaFillPass _fill = new AlphaFillPass();
        private readonly CrossfadePass _crossfade = new CrossfadePass();
        private readonly AlphaReadback _readback = new AlphaReadback();
        private object _deviceObj, _bbObj;
        private IntPtr _device, _rtv, _resource;
        private uint _width, _height;
        private int _format;
        private PropertyInfo _rtvProp, _resourceProp;

        // ======================================================================================== hook bodies

 /// <summary>
 /// Binds the <c>m_drawScene</c> accessors: a compiled getter (no boxing per frame), tried once on its first use (on
 /// the render thread, so <c>MyRender11</c>'s static constructor is never triggered from the update thread here),
 /// with reflection as the fallback; the setter is reflection (skipped frames only).
 /// </summary>
        public static void BindDrawScene(FieldInfo f)
        {
            DrawSceneField = f;
            _reflectDrawScene = () => (bool)f.GetValue(null);
            _clearDrawScene = () => f.SetValue(null, false);
            try { _compiledDrawScene = Expression.Lambda<Func<bool>>(Expression.Field(null, f)).Compile(); }
            catch (Exception) { _compiledDrawScene = null; }
            _getterState = _compiledDrawScene != null ? 0 : 2;
            _getDrawScene = ReadDrawScene;
        }

        private static Func<bool> _compiledDrawScene, _reflectDrawScene;
        private static int _getterState; // 0 untried, 1 compiled works, 2 reflection

        private static bool ReadDrawScene()
        {
            if (_getterState == 1) return _compiledDrawScene();
            if (_getterState == 2) return _reflectDrawScene();
            try
            {
                bool v = _compiledDrawScene();
                _getterState = 1;
                return v;
            }
            catch (Exception)
            {
                _getterState = 2;
                return _reflectDrawScene();
            }
        }

        public static void FullDrawPrefix()
        {
            RenderDisplay d = Display;
            if (d != null) d.OnFullDrawStart();
        }

        public static void FullDrawPostfix(bool draw)
        {
            RenderDisplay d = Display;
            if (d != null) d.OnFullDrawEnd(draw);
        }

        /// <summary>The 5-parameter <c>RenderMainSprites</c> prefix: the first call in a FullDraw decides the scene skip.</summary>
        public static void RenderMainSpritesPrefix()
        {
            RenderDisplay d = Display;
            Func<bool> get = _getDrawScene;
            if (d == null || get == null) return;
            if (d.DecideSkip(get())) _clearDrawScene();
        }

        public static void PresentPostfix()
        {
            RenderDisplay d = Display;
            if (d != null) d.OnPresented();
        }

        /// <summary>S7t T1: the <c>ConsumeMainSprites</c> prefix (SE's scene is in the backbuffer, the HUD sprites not yet).</summary>
        public static void ConsumeMainSpritesPrefix()
        {
            try
            {
                RenderDisplay d = Display;
                if (d != null) d.OnBeforeSprites();
            }
            catch (Exception) { }
        }

        /// <summary>
 /// S7t T1: installs the <c>ConsumeMainSprites</c> prefix (called from <c>S2Patches.Apply</c> with SE's Harmony and
 /// <c>VRageRender.MyRender11</c>); the shape is checked first (static, no parameter, void); a miss logs once and leaves
 /// the crossfade off (S7a's cut and flash).
 /// </summary>
        public static void PatchS7t(HarmonyLib.Harmony harmony, Type r11)
        {
            SpritesConsumeOk = false;
            try
            {
                if (harmony == null || r11 == null) { DeLog.Info("DE.Harmony MISSING S7t MyRender11 not found: crossfade off"); return; }
                MethodInfo m = r11.GetMethod("ConsumeMainSprites", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (m == null || !m.IsStatic || m.ReturnType != typeof(void))
                {
                    DeLog.Info("DE.Harmony MISSING S7t MyRender11.ConsumeMainSprites() (static void, no parameter) " + (m == null ? "not found" : "has another shape") + ": crossfade off");
                    return;
                }
                harmony.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(RenderHooks).GetMethod(nameof(ConsumeMainSpritesPrefix), BindingFlags.Static | BindingFlags.Public)));
                SpritesConsumeOk = true;
                S7tSettings.SpritesHookOk = true;
                DeLog.Info("DE.Harmony S7t OK MyRender11.ConsumeMainSprites() internal static prefix (the crossfade pass before the HUD sprites)");
            }
            catch (Exception e)
            {
                DeLog.Info("DE.Harmony MISSING S7t MyRender11.ConsumeMainSprites() patch failed: " + e.GetType().Name + ": " + e.Message + " (crossfade off)");
            }
        }

        public static void ModeChangedPostfix()
        {
            RenderDisplay d = Display;
            if (d != null) d.OnModeChanged();
        }

        // =============================================================================================== IRenderGpu

        public bool ReadbackPending { get { return _readback.Pending; } }
        public string FillError { get { return _fill.Error; } }
        public long FillExecutions { get { return _fill.Executions; } }

        public bool Fill(out string error)
        {
            error = null;
            if (!Refresh(out error)) return false;
            IntPtr ctx = IntPtr.Zero;
            try
            {
                ctx = D3D.GetImmediateContext(_device);
                if (_fill.Execute(_device, ctx, _rtv, _width, _height)) return true;
                error = _fill.Error;
                return false;
            }
            catch (Exception e) { error = e.GetType().Name + ": " + e.Message; return false; }
            finally { D3D.SafeRelease(ref ctx); }
        }

        // ============================================================================================= ICrossfadeGpu (S7t)

        public string CrossfadeError { get { return _crossfade.Error; } }
        public bool BackbufferSrgb { get { return D3D.IsSrgb(_format); } }
        public int BackbufferFormat { get { return _format; } }

        /// <summary>S7t T1: SE's backbuffer to (colour × f, <paramref name="alpha"/>), f premultiplying for DWM (<see cref="CrossfadeRules.ColourFactor"/>).</summary>
        public bool Fade(double alpha, bool premultiply, out string error)
        {
            error = null;
            if (!Refresh(out error)) return false;
            IntPtr ctx = IntPtr.Zero;
            try
            {
                ctx = D3D.GetImmediateContext(_device);
                float f = CrossfadeRules.ColourFactor(alpha, D3D.IsSrgb(_format), premultiply);
                if (_crossfade.Execute(_device, ctx, _rtv, _width, _height, f, (float)alpha)) return true;
                error = _crossfade.Error;
                return false;
            }
            catch (Exception e) { error = e.GetType().Name + ": " + e.Message; return false; }
            finally { D3D.SafeRelease(ref ctx); }
        }

        public bool BeginReadback(out string error)
        {
            error = null;
            if (!Refresh(out error)) return false;
            IntPtr ctx = IntPtr.Zero;
            try
            {
                ctx = D3D.GetImmediateContext(_device);
                if (_readback.BeginCopy(_device, ctx, _resource)) return true;
                error = _readback.Error;
                return false;
            }
            catch (Exception e) { error = e.GetType().Name + ": " + e.Message; return false; }
            finally { D3D.SafeRelease(ref ctx); }
        }

        public bool TryFinishReadback(out AlphaStats stats, out string error)
        {
            stats = default(AlphaStats);
            error = null;
            if (!_readback.Pending || _device == IntPtr.Zero) return false;
            IntPtr ctx = IntPtr.Zero;
            try
            {
                ctx = D3D.GetImmediateContext(_device);
                if (_readback.TryFinish(ctx, out stats)) return true;
                error = _readback.Error;
                return false;
            }
            catch (Exception e) { error = e.GetType().Name + ": " + e.Message; return false; }
            finally { D3D.SafeRelease(ref ctx); }
        }

        /// <summary>Re-reads the device and backbuffer pointers when SE's objects changed (device re-creation, resize).</summary>
        private bool Refresh(out string error)
        {
            error = null;
            if (DeviceProperty == null || BackbufferProperty == null) { error = "MyRender11.DeviceInstance/Backbuffer not resolved"; return false; }
            object dev = DeviceProperty.GetValue(null, null);
            object bb = BackbufferProperty.GetValue(null, null);
            if (dev == null || bb == null) { error = "no device or backbuffer yet"; return false; }
            if (!ReferenceEquals(dev, _deviceObj))
            {
                _deviceObj = dev;
                _device = NativePointer(dev);
            }
            if (!ReferenceEquals(bb, _bbObj))
            {
                _bbObj = bb;
                if (_rtvProp == null)
                {
                    _rtvProp = bb.GetType().GetProperty("Rtv", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _resourceProp = bb.GetType().GetProperty("Resource", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                object rtv = _rtvProp != null ? _rtvProp.GetValue(bb, null) : null;
                object res = _resourceProp != null ? _resourceProp.GetValue(bb, null) : null;
                _rtv = rtv != null ? NativePointer(rtv) : IntPtr.Zero;
                _resource = res != null ? NativePointer(res) : IntPtr.Zero;
                _width = _height = 0;
                _format = 0;
                if (_resource != IntPtr.Zero)
                {
                    IntPtr tex;
                    if (D3D.QueryInterface(_resource, D3D.IidTexture2D, out tex) >= 0 && tex != IntPtr.Zero)
                    {
                        D3D.Texture2DDesc desc;
                        D3D.GetDesc(tex, out desc);
                        _width = desc.Width;
                        _height = desc.Height;
                        _format = desc.Format; // S7t: the RTV's format (created without a description)
                        D3D.Release(tex);
                    }
                }
            }
            if (_device == IntPtr.Zero || _rtv == IntPtr.Zero || _resource == IntPtr.Zero || _width == 0)
            {
                error = "backbuffer pointers unavailable (device " + NativeWin.Hex(_device) + ", rtv " + NativeWin.Hex(_rtv) + ", size " + _width + "x" + _height + ")";
                _bbObj = null; // try again next frame
                return false;
            }
            return true;
        }

        private static IntPtr NativePointer(object sharpDx)
        {
            PropertyInfo p = sharpDx.GetType().GetProperty("NativePointer", BindingFlags.Instance | BindingFlags.Public);
            if (p == null || p.PropertyType != typeof(IntPtr)) return IntPtr.Zero;
            return (IntPtr)p.GetValue(sharpDx, null);
        }
    }
}
