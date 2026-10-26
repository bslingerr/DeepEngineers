using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// The HYPOTHESIS units of the S2 display that touch Direct3D 11 (<see cref="AlphaFillPass"/>,
 /// <see cref="AlphaReadback"/>), run on a WARP (software) device created here: every vtable slot they call is executed
 /// once without the game, SE's render-target format (R8G8B8A8_UNorm_SRgb) is used, and the caller's pipeline state is
 /// checked to come back unchanged after the pass (SwapDeviceContextState). What the real run still decides: that
 /// SE's frame leaves the backbuffer where this pass finds it, and what DWM composes.
 /// </summary>
    [TestClass]
    public unsafe class S2D3DTests
    {
        private const int DriverTypeWarp = 5;

        [DllImport("d3d11.dll", ExactSpelling = true)]
        private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
            uint numLevels, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

        private IntPtr _device, _ctx, _tex, _rtv;

        [TestCleanup]
        public void Cleanup()
        {
            D3D.SafeRelease(ref _rtv);
            D3D.SafeRelease(ref _tex);
            D3D.SafeRelease(ref _ctx);
            D3D.SafeRelease(ref _device);
        }

        private void CreateTarget(uint w, uint h, int format)
        {
            int level;
            int hr = D3D11CreateDevice(IntPtr.Zero, DriverTypeWarp, IntPtr.Zero, 0, IntPtr.Zero, 0, (uint)D3D.SdkVersion, out _device, out level, out _ctx);
            if (hr < 0 || _device == IntPtr.Zero) Assert.Inconclusive("no WARP device here (hr " + NativeWin.Hr(hr) + ")");
            var d = new D3D.Texture2DDesc
            {
                Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = format, SampleCount = 1,
                Usage = D3D.UsageDefault, BindFlags = D3D.BindRenderTarget,
            };
            Assert.IsTrue(D3D.CreateTexture2D(_device, ref d, out _tex) >= 0, "CreateTexture2D");
            Assert.IsTrue(D3D.CreateRenderTargetView(_device, _tex, out _rtv) >= 0, "CreateRenderTargetView");
        }

        /// <summary>The raw RGBA bytes of the target (a test-side readback through the same slots).</summary>
        private byte[] ReadBytes(uint w, uint h, int format)
        {
            var s = new D3D.Texture2DDesc { Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = format, SampleCount = 1, Usage = D3D.UsageStaging, CPUAccessFlags = D3D.CpuAccessRead };
            IntPtr staging;
            Assert.IsTrue(D3D.CreateTexture2D(_device, ref s, out staging) >= 0);
            try
            {
                D3D.CopyResource(_ctx, staging, _tex);
                D3D.MappedSubresource m;
                Assert.IsTrue(D3D.Map(_ctx, staging, D3D.MapRead, 0u, out m) >= 0, "Map");
                var bytes = new byte[w * h * 4];
                for (uint y = 0; y < h; y++)
                    Marshal.Copy(m.Data + (int)(y * m.RowPitch), bytes, (int)(y * w * 4), (int)(w * 4));
                D3D.Unmap(_ctx, staging);
                return bytes;
            }
            finally { D3D.Release(staging); }
        }

        [TestMethod]
        public void AlphaFill_SetsAlphaTo1_KeepsColour_RestoresTheCallersState()
        {
            const uint W = 64, H = 40;
            CreateTarget(W, H, D3D.FormatR8G8B8A8UnormSrgb);
            D3D.ClearRenderTargetView(_ctx, _rtv, 0.2f, 0.4f, 0.6f, 0.25f);
            byte[] before = ReadBytes(W, H, D3D.FormatR8G8B8A8UnormSrgb);
            Assert.AreEqual(64, before[3], "alpha 0.25 stored as 64 (alpha is linear in an sRGB target)");

            // the caller's state (what SE's render context believes is bound)
            D3D.RSSetViewport(_ctx, new D3D.Viewport { TopLeftX = 3, TopLeftY = 4, Width = 10, Height = 11, MaxDepth = 1 });
            D3D.OMSetRenderTarget(_ctx, _rtv);

            using (var pass = new AlphaFillPass())
            {
                Assert.IsTrue(pass.Execute(_device, _ctx, _rtv, W, H), pass.Error);
                Assert.IsTrue(pass.Execute(_device, _ctx, _rtv, W, H), "a second frame reuses the objects");
                Assert.AreEqual(2, pass.Executions);
            }

            D3D.Viewport vp;
            Assert.AreEqual(1u, D3D.RSGetViewport(_ctx, out vp));
            Assert.IsTrue(vp.TopLeftX == 3 && vp.TopLeftY == 4 && vp.Width == 10 && vp.Height == 11, "viewport restored");
            IntPtr bound = D3D.OMGetRenderTarget(_ctx);
            Assert.AreEqual(_rtv, bound, "render target restored");
            D3D.Release(bound);

            byte[] after = ReadBytes(W, H, D3D.FormatR8G8B8A8UnormSrgb);
            for (int i = 0; i < after.Length; i += 4)
            {
                Assert.AreEqual(255, after[i + 3], "alpha at pixel " + i / 4);
                Assert.AreEqual(before[i], after[i], "R unchanged");
                Assert.AreEqual(before[i + 1], after[i + 1], "G unchanged");
                Assert.AreEqual(before[i + 2], after[i + 2], "B unchanged");
            }
        }

        [TestMethod]
        public void AlphaReadback_CountsAlpha_ThroughStaging()
        {
            const uint W = 32, H = 16;
            CreateTarget(W, H, D3D.FormatR8G8B8A8UnormSrgb);
            D3D.ClearRenderTargetView(_ctx, _rtv, 0f, 0f, 0f, 0f); // what design F's skipped scene leaves
            using (var rb = new AlphaReadback())
            {
                Assert.IsTrue(rb.BeginCopy(_device, _ctx, _tex), rb.Error);
                Assert.IsTrue(rb.Pending);
                AlphaStats s = default(AlphaStats);
                bool done = false;
                for (int i = 0; i < AlphaReadback.MaxWaitFrames + 2 && !done; i++) done = rb.TryFinish(_ctx, out s);
                Assert.IsTrue(done, rb.Error);
                Assert.IsFalse(rb.Pending);
                Assert.AreEqual(W * H, (uint)s.Pixels);
                Assert.AreEqual(0, s.AlphaGt0);
                Assert.AreEqual(s.Pixels, s.Alpha0);
                Assert.AreEqual(s.Pixels, s.AlphaLt1);

                using (var pass = new AlphaFillPass()) Assert.IsTrue(pass.Execute(_device, _ctx, _rtv, W, H), pass.Error);
                Assert.IsTrue(rb.BeginCopy(_device, _ctx, _tex));
                done = false;
                for (int i = 0; i < AlphaReadback.MaxWaitFrames + 2 && !done; i++) done = rb.TryFinish(_ctx, out s);
                Assert.IsTrue(done);
                Assert.AreEqual(s.Pixels, s.AlphaGt0);
                Assert.AreEqual(0, s.AlphaLt1, "after the fill no pixel has alpha < 1 (the T-D1 Prime criterion < 0.1 %)");
                Assert.AreEqual(0.0, s.FractionLt1);
            }
        }

        [TestMethod]
        public void AlphaCount_OnASyntheticImage_AndFormats()
        {
            // 3 x 2 pixels, row pitch 16 (padding), alpha 0, 128, 255 / 255, 255, 0
            var img = new byte[32];
            byte[] alphas = { 0, 128, 255, 255, 255, 0 };
            for (int y = 0; y < 2; y++)
                for (int x = 0; x < 3; x++) img[y * 16 + x * 4 + 3] = alphas[y * 3 + x];
            var s = new AlphaStats();
            fixed (byte* p = img) AlphaReadback.Count(p, 16, 3, 2, 3, ref s);
            Assert.AreEqual(6, s.Pixels);
            Assert.AreEqual(4, s.AlphaGt0);
            Assert.AreEqual(2, s.Alpha0);
            Assert.AreEqual(3, s.AlphaLt1);
            Assert.AreEqual(3, D3D.AlphaByte(D3D.FormatR8G8B8A8UnormSrgb));
            Assert.AreEqual(3, D3D.AlphaByte(D3D.FormatB8G8R8A8Unorm));
            Assert.AreEqual(-1, D3D.AlphaByte(10), "R16G16B16A16_FLOAT is not counted");
            Assert.AreEqual(264, Marshal.SizeOf(typeof(D3D.BlendDesc)), "D3D11_BLEND_DESC");
            Assert.AreEqual(40, Marshal.SizeOf(typeof(D3D.RasterizerDesc)), "D3D11_RASTERIZER_DESC");
            Assert.AreEqual(52, Marshal.SizeOf(typeof(D3D.DepthStencilDesc)), "D3D11_DEPTH_STENCIL_DESC");
            Assert.AreEqual(44, Marshal.SizeOf(typeof(D3D.Texture2DDesc)), "D3D11_TEXTURE2D_DESC");
            Assert.AreEqual(24, Marshal.SizeOf(typeof(D3D.Viewport)), "D3D11_VIEWPORT");
            Assert.AreEqual(16, Marshal.SizeOf(typeof(D3D.MappedSubresource)), "D3D11_MAPPED_SUBRESOURCE");
        }
    }
}
