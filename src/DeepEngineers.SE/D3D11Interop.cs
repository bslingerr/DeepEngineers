using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DeepEngineers.SE
{
    /// <summary>
 /// Minimal raw Direct3D 11 COM access through vtable slots (no SharpDX reference; game-independent). Used only by the
 /// HYPOTHESIS units of S2 on SE's render thread: the Prime alpha fill (<see cref="AlphaFillPass"/>) and the dev-only
 /// backbuffer alpha readback (<see cref="AlphaReadback"/>); since S7t T1 the crossfade pass (<see cref="CrossfadePass"/>,
 /// the same slots plus OMSetBlendState's blend factor and D3D11_BLEND_BLEND_FACTOR). tests/DeepEngineers.SE.Tests runs both against a WARP
 /// device created here, so every slot below is executed once without the game.
 /// <para>
 /// Slots are the method positions in the C vtables of the Windows SDK 10.0.22621 headers (<c>d3d11.h</c>
 /// <c>ID3D11DeviceVtbl</c>, <c>ID3D11DeviceContextVtbl</c>, <c>ID3D11Texture2DVtbl</c>, <c>ID3D11RenderTargetViewVtbl</c>;
 /// <c>d3d11_1.h</c> <c>ID3D11Device1Vtbl</c>, <c>ID3D11DeviceContext1Vtbl</c>; <c>d3dcommon.h</c> <c>ID3D10BlobVtbl</c>),
 /// counted by a script over the headers. IIDs from the same headers.
 /// </para>
 /// </summary>
    internal static unsafe class D3D
    {
        // ---- vtable slots (d3d11.h / d3d11_1.h / d3dcommon.h, C interface order)
        public const int QueryInterfaceSlot = 0, AddRefSlot = 1, ReleaseSlot = 2;
        public const int DevCreateTexture2D = 5, DevCreateRenderTargetView = 9, DevCreateVertexShader = 12, DevCreatePixelShader = 15,
            DevCreateBlendState = 20, DevCreateDepthStencilState = 21, DevCreateRasterizerState = 22, DevGetFeatureLevel = 37,
            DevGetImmediateContext = 40;
        public const int Dev1CreateDeviceContextState = 47;
        public const int CtxPSSetShader = 9, CtxVSSetShader = 11, CtxDraw = 13, CtxMap = 14, CtxUnmap = 15, CtxIASetInputLayout = 17,
            CtxIASetPrimitiveTopology = 24, CtxOMSetRenderTargets = 33, CtxOMSetBlendState = 35, CtxOMSetDepthStencilState = 36,
            CtxRSSetState = 43, CtxRSSetViewports = 44, CtxCopyResource = 47, CtxClearRenderTargetView = 50,
            CtxOMGetRenderTargets = 89, CtxRSGetViewports = 95;
        public const int Ctx1SwapDeviceContextState = 131;
        public const int Tex2DGetDesc = 10;
        public const int ViewGetResource = 7;
        public const int BlobGetBufferPointer = 3, BlobGetBufferSize = 4;

        // ---- IIDs (MIDL_INTERFACE strings of the same headers)
        public static readonly Guid IidDevice = new Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140");
        public static readonly Guid IidDevice1 = new Guid("a04bfb29-08ef-43d6-a49c-a9bdbdcbe686");
        public static readonly Guid IidDeviceContext1 = new Guid("bb2c6faa-b5fb-4082-8e6b-388b8cfa90e1");
        public static readonly Guid IidTexture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        // ---- constants (d3d11.h, dxgiformat.h, winerror.h)
        public const int SdkVersion = 7;
        public const int FillSolid = 3, CullNone = 1, ComparisonAlways = 8, StencilOpKeep = 1, BlendZero = 1, BlendOne = 2, BlendOpAdd = 1;
        /// <summary>S7t T1: D3D11_BLEND_BLEND_FACTOR (d3d11.h <c>D3D11_BLEND</c>: ZERO 1, ONE 2, … SRC_ALPHA_SAT 11, BLEND_FACTOR 14,
 /// INV_BLEND_FACTOR 15): the factor given to OMSetBlendState (its alpha component on the alpha channel).</summary>
        public const int BlendBlendFactor = 14;
        public const byte ColorWriteAlpha = 8, ColorWriteAll = 15;
        public const int TopologyTriangleList = 4;
        public const int UsageDefault = 0, UsageStaging = 3;
        public const uint BindRenderTarget = 0x20, CpuAccessRead = 0x20000;
        public const int MapRead = 1;
        public const uint MapFlagDoNotWait = 0x100000;
        public const int DxgiErrorWasStillDrawing = unchecked((int)0x887A000A);
        public const int FormatR8G8B8A8Typeless = 27, FormatR8G8B8A8Unorm = 28, FormatR8G8B8A8UnormSrgb = 29,
            FormatB8G8R8A8Unorm = 87, FormatB8G8R8A8Typeless = 90, FormatB8G8R8A8UnormSrgb = 91;

        [StructLayout(LayoutKind.Sequential)]
        public struct Viewport { public float TopLeftX, TopLeftY, Width, Height, MinDepth, MaxDepth; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RenderTargetBlendDesc
        {
            public int BlendEnable, SrcBlend, DestBlend, BlendOp, SrcBlendAlpha, DestBlendAlpha, BlendOpAlpha;
            public byte RenderTargetWriteMask;
        }

        /// <summary>D3D11_BLEND_DESC: two BOOLs and eight render-target descriptions (8 + 8 × 32 = 264 bytes).</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct BlendDesc
        {
            public int AlphaToCoverageEnable, IndependentBlendEnable;
            public RenderTargetBlendDesc Rt0, Rt1, Rt2, Rt3, Rt4, Rt5, Rt6, Rt7;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RasterizerDesc
        {
            public int FillMode, CullMode, FrontCounterClockwise, DepthBias;
            public float DepthBiasClamp, SlopeScaledDepthBias;
            public int DepthClipEnable, ScissorEnable, MultisampleEnable, AntialiasedLineEnable;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DepthStencilOpDesc { public int StencilFailOp, StencilDepthFailOp, StencilPassOp, StencilFunc; }

        [StructLayout(LayoutKind.Sequential)]
        public struct DepthStencilDesc
        {
            public int DepthEnable, DepthWriteMask, DepthFunc, StencilEnable;
            public byte StencilReadMask, StencilWriteMask;
            public DepthStencilOpDesc FrontFace, BackFace;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Texture2DDesc
        {
            public uint Width, Height, MipLevels, ArraySize;
            public int Format;
            public uint SampleCount, SampleQuality;
            public int Usage;
            public uint BindFlags, CPUAccessFlags, MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MappedSubresource { public IntPtr Data; public uint RowPitch, DepthPitch; }

        // ================================================================================================ IUnknown

        public static void* Slot(IntPtr obj, int index) { return (*(void***)obj)[index]; }

        public static int QueryInterface(IntPtr obj, Guid iid, out IntPtr result)
        {
            IntPtr r = IntPtr.Zero;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(obj, QueryInterfaceSlot))(obj, &iid, &r);
            result = r;
            return hr;
        }

        public static uint AddRef(IntPtr obj)
        {
            return obj == IntPtr.Zero ? 0u : ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(obj, AddRefSlot))(obj);
        }

        public static uint Release(IntPtr obj)
        {
            return obj == IntPtr.Zero ? 0u : ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(obj, ReleaseSlot))(obj);
        }

        public static void SafeRelease(ref IntPtr obj)
        {
            IntPtr o = obj;
            obj = IntPtr.Zero;
            if (o != IntPtr.Zero) Release(o);
        }

        // ================================================================================================== device

        public static int GetFeatureLevel(IntPtr device)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(device, DevGetFeatureLevel))(device);
        }

        /// <summary>ID3D11Device:GetImmediateContext (the returned context is AddRef'd: release it).</summary>
        public static IntPtr GetImmediateContext(IntPtr device)
        {
            IntPtr ctx = IntPtr.Zero;
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)Slot(device, DevGetImmediateContext))(device, &ctx);
            return ctx;
        }

        public static int CreateVertexShader(IntPtr device, IntPtr code, UIntPtr size, out IntPtr shader)
        {
            IntPtr s = IntPtr.Zero;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, UIntPtr, IntPtr, IntPtr*, int>)Slot(device, DevCreateVertexShader))(device, code, size, IntPtr.Zero, &s);
            shader = s;
            return hr;
        }

        public static int CreatePixelShader(IntPtr device, IntPtr code, UIntPtr size, out IntPtr shader)
        {
            IntPtr s = IntPtr.Zero;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, UIntPtr, IntPtr, IntPtr*, int>)Slot(device, DevCreatePixelShader))(device, code, size, IntPtr.Zero, &s);
            shader = s;
            return hr;
        }

        public static int CreateBlendState(IntPtr device, ref BlendDesc desc, out IntPtr state)
        {
            IntPtr s = IntPtr.Zero;
            int hr;
            fixed (BlendDesc* d = &desc)
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, BlendDesc*, IntPtr*, int>)Slot(device, DevCreateBlendState))(device, d, &s);
            state = s;
            return hr;
        }

        public static int CreateRasterizerState(IntPtr device, ref RasterizerDesc desc, out IntPtr state)
        {
            IntPtr s = IntPtr.Zero;
            int hr;
            fixed (RasterizerDesc* d = &desc)
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, RasterizerDesc*, IntPtr*, int>)Slot(device, DevCreateRasterizerState))(device, d, &s);
            state = s;
            return hr;
        }

        public static int CreateDepthStencilState(IntPtr device, ref DepthStencilDesc desc, out IntPtr state)
        {
            IntPtr s = IntPtr.Zero;
            int hr;
            fixed (DepthStencilDesc* d = &desc)
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, DepthStencilDesc*, IntPtr*, int>)Slot(device, DevCreateDepthStencilState))(device, d, &s);
            state = s;
            return hr;
        }

        public static int CreateTexture2D(IntPtr device, ref Texture2DDesc desc, out IntPtr texture)
        {
            IntPtr t = IntPtr.Zero;
            int hr;
            fixed (Texture2DDesc* d = &desc)
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, IntPtr, IntPtr*, int>)Slot(device, DevCreateTexture2D))(device, d, IntPtr.Zero, &t);
            texture = t;
            return hr;
        }

        /// <summary>ID3D11Device:CreateRenderTargetView with a null description (the resource's own format; tests).</summary>
        public static int CreateRenderTargetView(IntPtr device, IntPtr resource, out IntPtr rtv)
        {
            IntPtr v = IntPtr.Zero;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)Slot(device, DevCreateRenderTargetView))(device, resource, IntPtr.Zero, &v);
            rtv = v;
            return hr;
        }

        /// <summary>ID3D11Device1:CreateDeviceContextState (flags 0: SE's device is created without SINGLETHREADED).</summary>
        public static int CreateDeviceContextState(IntPtr device1, int featureLevel, Guid emulatedInterface, out IntPtr state)
        {
            IntPtr s = IntPtr.Zero;
            int chosen = 0;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, int*, uint, uint, Guid*, int*, IntPtr*, int>)Slot(device1, Dev1CreateDeviceContextState))(
                device1, 0u, &featureLevel, 1u, (uint)SdkVersion, &emulatedInterface, &chosen, &s);
            state = s;
            return hr;
        }

        // ================================================================================================= context

        public static void IASetPrimitiveTopology(IntPtr ctx, int topology)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, int, void>)Slot(ctx, CtxIASetPrimitiveTopology))(ctx, topology);
        }

        public static void IASetInputLayout(IntPtr ctx, IntPtr layout)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)Slot(ctx, CtxIASetInputLayout))(ctx, layout);
        }

        public static void VSSetShader(IntPtr ctx, IntPtr shader)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, void>)Slot(ctx, CtxVSSetShader))(ctx, shader, IntPtr.Zero, 0u);
        }

        public static void PSSetShader(IntPtr ctx, IntPtr shader)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, void>)Slot(ctx, CtxPSSetShader))(ctx, shader, IntPtr.Zero, 0u);
        }

        public static void RSSetState(IntPtr ctx, IntPtr state)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)Slot(ctx, CtxRSSetState))(ctx, state);
        }

        public static void RSSetViewport(IntPtr ctx, Viewport vp)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, Viewport*, void>)Slot(ctx, CtxRSSetViewports))(ctx, 1u, &vp);
        }

        /// <summary>ID3D11DeviceContext:RSGetViewports for one viewport (tests): the count bound, and the first.</summary>
        public static uint RSGetViewport(IntPtr ctx, out Viewport vp)
        {
            uint n = 1;
            Viewport v = default(Viewport);
            ((delegate* unmanaged[Stdcall]<IntPtr, uint*, Viewport*, void>)Slot(ctx, CtxRSGetViewports))(ctx, &n, &v);
            vp = v;
            return n;
        }

        public static void OMSetBlendState(IntPtr ctx, IntPtr state)
        {
            OMSetBlendState(ctx, state, 1f, 1f, 1f, 1f);
        }

        /// <summary>S7t T1: ID3D11DeviceContext:OMSetBlendState (slot 35) with an explicit blend factor, sample mask all.</summary>
        public static void OMSetBlendState(IntPtr ctx, IntPtr state, float r, float g, float b, float a)
        {
            float* factor = stackalloc float[4];
            factor[0] = r; factor[1] = g; factor[2] = b; factor[3] = a;
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, float*, uint, void>)Slot(ctx, CtxOMSetBlendState))(ctx, state, factor, 0xFFFFFFFFu);
        }

        public static void OMSetDepthStencilState(IntPtr ctx, IntPtr state)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Slot(ctx, CtxOMSetDepthStencilState))(ctx, state, 0u);
        }

        /// <summary>Binds one render target (or none when <paramref name="rtv"/> is Zero), no depth view.</summary>
        public static void OMSetRenderTarget(IntPtr ctx, IntPtr rtv)
        {
            IntPtr v = rtv;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, IntPtr, void>)Slot(ctx, CtxOMSetRenderTargets))(ctx, rtv == IntPtr.Zero ? 0u : 1u, rtv == IntPtr.Zero ? null : &v, IntPtr.Zero);
        }

        /// <summary>ID3D11DeviceContext:OMGetRenderTargets for slot 0 (tests; the view is AddRef'd: release it).</summary>
        public static IntPtr OMGetRenderTarget(IntPtr ctx)
        {
            IntPtr v = IntPtr.Zero;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, IntPtr*, void>)Slot(ctx, CtxOMGetRenderTargets))(ctx, 1u, &v, null);
            return v;
        }

        public static void Draw(IntPtr ctx, uint vertexCount)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, void>)Slot(ctx, CtxDraw))(ctx, vertexCount, 0u);
        }

        public static void CopyResource(IntPtr ctx, IntPtr dst, IntPtr src)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Slot(ctx, CtxCopyResource))(ctx, dst, src);
        }

        public static int Map(IntPtr ctx, IntPtr resource, int mapType, uint flags, out MappedSubresource mapped)
        {
            MappedSubresource m = default(MappedSubresource);
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, MappedSubresource*, int>)Slot(ctx, CtxMap))(ctx, resource, 0u, mapType, flags, &m);
            mapped = m;
            return hr;
        }

        public static void Unmap(IntPtr ctx, IntPtr resource)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Slot(ctx, CtxUnmap))(ctx, resource, 0u);
        }

        public static void ClearRenderTargetView(IntPtr ctx, IntPtr rtv, float r, float g, float b, float a)
        {
            float* c = stackalloc float[4];
            c[0] = r; c[1] = g; c[2] = b; c[3] = a;
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, float*, void>)Slot(ctx, CtxClearRenderTargetView))(ctx, rtv, c);
        }

        /// <summary>ID3D11DeviceContext1:SwapDeviceContextState (<paramref name="previous"/> may be null).</summary>
        public static void SwapDeviceContextState(IntPtr ctx1, IntPtr state, IntPtr* previous)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, void>)Slot(ctx1, Ctx1SwapDeviceContextState))(ctx1, state, previous);
        }

        // ================================================================================================ resources

        public static void GetDesc(IntPtr texture2D, out Texture2DDesc desc)
        {
            Texture2DDesc d = default(Texture2DDesc);
            ((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, void>)Slot(texture2D, Tex2DGetDesc))(texture2D, &d);
            desc = d;
        }

        /// <summary>ID3D11View:GetResource (AddRef'd: release it).</summary>
        public static IntPtr GetViewResource(IntPtr view)
        {
            IntPtr r = IntPtr.Zero;
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)Slot(view, ViewGetResource))(view, &r);
            return r;
        }

        // =================================================================================================== shaders

        [DllImport("d3dcompiler_47.dll", ExactSpelling = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
        private static extern int D3DCompile(byte[] src, UIntPtr size, string sourceName, IntPtr defines, IntPtr include,
            string entryPoint, string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);

        /// <summary>D3DCOMPILE_OPTIMIZATION_LEVEL3 (d3dcompiler.h).</summary>
        public const uint CompileOptimizationLevel3 = 1u << 15;

        /// <summary>Compiles HLSL with d3dcompiler_47 (shipped with SE in Bin64 and with Windows); the blob is AddRef'd.</summary>
        public static int Compile(string hlsl, string entry, string target, out IntPtr blob, out string errors)
        {
            blob = IntPtr.Zero;
            errors = null;
            byte[] src = Encoding.ASCII.GetBytes(hlsl);
            IntPtr code, err;
            int hr = D3DCompile(src, (UIntPtr)src.Length, "deepengineers_s2", IntPtr.Zero, IntPtr.Zero, entry, target, CompileOptimizationLevel3, 0u, out code, out err);
            if (err != IntPtr.Zero)
            {
                try { errors = Marshal.PtrToStringAnsi(BlobPointer(err), (int)BlobSize(err).ToUInt64()); }
                catch (Exception) { errors = "(unreadable)"; }
                Release(err);
            }
            if (hr < 0) { SafeRelease(ref code); return hr; }
            blob = code;
            return hr;
        }

        public static IntPtr BlobPointer(IntPtr blob)
        {
            return (IntPtr)((delegate* unmanaged[Stdcall]<IntPtr, void*>)Slot(blob, BlobGetBufferPointer))(blob);
        }

        public static UIntPtr BlobSize(IntPtr blob)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, UIntPtr>)Slot(blob, BlobGetBufferSize))(blob);
        }

        /// <summary>S7t T1: an sRGB-encoded 8-bit format (the blend runs on linear values and is re-encoded).</summary>
        public static bool IsSrgb(int format)
        {
            return format == FormatR8G8B8A8UnormSrgb || format == FormatB8G8R8A8UnormSrgb;
        }

        /// <summary>Byte offset of the alpha channel for the 8-bit RGBA/BGRA formats (3), or −1.</summary>
        public static int AlphaByte(int format)
        {
            switch (format)
            {
                case FormatR8G8B8A8Typeless: case FormatR8G8B8A8Unorm: case FormatR8G8B8A8UnormSrgb:
                case FormatB8G8R8A8Unorm: case FormatB8G8R8A8Typeless: case FormatB8G8R8A8UnormSrgb:
                    return 3;
                default:
                    return -1;
            }
        }
    }

    /// <summary>
 /// The Prime alpha fill (ARCHITECTURE §5.1 "Alpha fill", §4.4; HYPOTHESIS mechanism until the S2.T-D1 readback): one
 /// full-screen triangle after SE's sprites and before <c>Present</c>, colour write mask = alpha only, pixel shader
 /// output alpha 1, so translucent HUD sprites over the opaque scene leave every backbuffer pixel at alpha 1 while the
 /// DWM margins are on. The pass runs inside its own <c>ID3DDeviceContextState</c>
 /// (<c>ID3D11DeviceContext1:SwapDeviceContextState</c>): SE's whole pipeline state is swapped out and back in
 /// unchanged, so SE's cached render-context state stays true, and the state object keeps no reference to the
 /// backbuffer (render target unbound before swapping back; a held reference would make <c>ResizeBuffers</c> fail).
 /// Game-independent; one instance per device (re-created when the device pointer changes). Render thread only.
 /// </summary>
    internal sealed unsafe class AlphaFillPass : IDisposable
    {
        public const string VertexShader =
            "float4 main(uint id : SV_VertexID) : SV_Position\n" +
            "{\n" +
            "    float2 uv = float2((id << 1) & 2, id & 2);\n" +
            "    return float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);\n" +
            "}\n";

        public const string PixelShader =
            "float4 main() : SV_Target\n" +
            "{\n" +
            "    return float4(0.0, 0.0, 0.0, 1.0);\n" +
            "}\n";

        private IntPtr _device, _vs, _ps, _blend, _rs, _dss, _state;
        private bool _failed;

        /// <summary>Why the pass is unavailable (null while fine).</summary>
        public string Error { get; private set; }
        public long Executions { get; private set; }
        public bool Ready { get { return _state != IntPtr.Zero && !_failed; } }

        /// <summary>Creates the shaders and states for <paramref name="device"/> (an ID3D11Device1-capable device). False on failure (logged once by the caller through <see cref="Error"/>).</summary>
        public bool Ensure(IntPtr device)
        {
            if (device == IntPtr.Zero) { Error = "no device"; return false; }
            if (device == _device) return Ready;
            ReleaseAll();
            _device = device;
            _failed = true;
            IntPtr device1 = IntPtr.Zero, vsBlob = IntPtr.Zero, psBlob = IntPtr.Zero;
            try
            {
                string err;
                int hr = D3D.Compile(VertexShader, "main", "vs_4_0", out vsBlob, out err);
                if (hr < 0) { Error = "vertex shader compile " + NativeWin.Hr(hr) + " " + err; return false; }
                hr = D3D.Compile(PixelShader, "main", "ps_4_0", out psBlob, out err);
                if (hr < 0) { Error = "pixel shader compile " + NativeWin.Hr(hr) + " " + err; return false; }
                hr = D3D.CreateVertexShader(device, D3D.BlobPointer(vsBlob), D3D.BlobSize(vsBlob), out _vs);
                if (hr < 0) { Error = "CreateVertexShader " + NativeWin.Hr(hr); return false; }
                hr = D3D.CreatePixelShader(device, D3D.BlobPointer(psBlob), D3D.BlobSize(psBlob), out _ps);
                if (hr < 0) { Error = "CreatePixelShader " + NativeWin.Hr(hr); return false; }

                var bd = new D3D.BlendDesc();
                D3D.RenderTargetBlendDesc rt = new D3D.RenderTargetBlendDesc
                {
                    BlendEnable = 0, SrcBlend = D3D.BlendOne, DestBlend = D3D.BlendZero, BlendOp = D3D.BlendOpAdd,
                    SrcBlendAlpha = D3D.BlendOne, DestBlendAlpha = D3D.BlendZero, BlendOpAlpha = D3D.BlendOpAdd,
                    RenderTargetWriteMask = D3D.ColorWriteAlpha,
                };
                D3D.RenderTargetBlendDesc all = rt;
                all.RenderTargetWriteMask = D3D.ColorWriteAll;
                bd.Rt0 = rt; bd.Rt1 = all; bd.Rt2 = all; bd.Rt3 = all; bd.Rt4 = all; bd.Rt5 = all; bd.Rt6 = all; bd.Rt7 = all;
                hr = D3D.CreateBlendState(device, ref bd, out _blend);
                if (hr < 0) { Error = "CreateBlendState " + NativeWin.Hr(hr); return false; }

                var rd = new D3D.RasterizerDesc { FillMode = D3D.FillSolid, CullMode = D3D.CullNone, DepthClipEnable = 1 };
                hr = D3D.CreateRasterizerState(device, ref rd, out _rs);
                if (hr < 0) { Error = "CreateRasterizerState " + NativeWin.Hr(hr); return false; }

                var op = new D3D.DepthStencilOpDesc { StencilFailOp = D3D.StencilOpKeep, StencilDepthFailOp = D3D.StencilOpKeep, StencilPassOp = D3D.StencilOpKeep, StencilFunc = D3D.ComparisonAlways };
                var dd = new D3D.DepthStencilDesc { DepthEnable = 0, DepthWriteMask = 0, DepthFunc = D3D.ComparisonAlways, StencilEnable = 0, StencilReadMask = 0xFF, StencilWriteMask = 0xFF, FrontFace = op, BackFace = op };
                hr = D3D.CreateDepthStencilState(device, ref dd, out _dss);
                if (hr < 0) { Error = "CreateDepthStencilState " + NativeWin.Hr(hr); return false; }

                hr = D3D.QueryInterface(device, D3D.IidDevice1, out device1);
                if (hr < 0 || device1 == IntPtr.Zero) { Error = "device is not ID3D11Device1 " + NativeWin.Hr(hr); return false; }
                hr = D3D.CreateDeviceContextState(device1, D3D.GetFeatureLevel(device), D3D.IidDevice1, out _state);
                if (hr < 0) { Error = "CreateDeviceContextState " + NativeWin.Hr(hr); return false; }

                _failed = false;
                Error = null;
                return true;
            }
            catch (Exception e)
            {
                Error = "setup " + e.GetType().Name + ": " + e.Message;
                return false;
            }
            finally
            {
                D3D.SafeRelease(ref device1);
                D3D.SafeRelease(ref vsBlob);
                D3D.SafeRelease(ref psBlob);
                if (_failed) ReleaseObjects();
            }
        }

        /// <summary>
 /// Fills the alpha of <paramref name="rtv"/> (width × height) with 1 on <paramref name="context"/>, leaving its
 /// colour and the caller's pipeline state unchanged.
 /// </summary>
        public bool Execute(IntPtr device, IntPtr context, IntPtr rtv, uint width, uint height)
        {
            if (context == IntPtr.Zero || rtv == IntPtr.Zero || width == 0 || height == 0) { Error = "no context, view or size"; return false; }
            if (!Ensure(device)) return false;
            IntPtr ctx1 = IntPtr.Zero, previous = IntPtr.Zero;
            try
            {
                int hr = D3D.QueryInterface(context, D3D.IidDeviceContext1, out ctx1);
                if (hr < 0 || ctx1 == IntPtr.Zero) { Error = "context is not ID3D11DeviceContext1 " + NativeWin.Hr(hr); _failed = true; return false; }
                D3D.SwapDeviceContextState(ctx1, _state, &previous);
                try
                {
                    D3D.IASetPrimitiveTopology(ctx1, D3D.TopologyTriangleList);
                    D3D.IASetInputLayout(ctx1, IntPtr.Zero);
                    D3D.VSSetShader(ctx1, _vs);
                    D3D.PSSetShader(ctx1, _ps);
                    D3D.RSSetState(ctx1, _rs);
                    D3D.RSSetViewport(ctx1, new D3D.Viewport { Width = width, Height = height, MaxDepth = 1f });
                    D3D.OMSetBlendState(ctx1, _blend);
                    D3D.OMSetDepthStencilState(ctx1, _dss);
                    D3D.OMSetRenderTarget(ctx1, rtv);
                    D3D.Draw(ctx1, 3);
                    D3D.OMSetRenderTarget(ctx1, IntPtr.Zero); // our state object keeps no reference to the backbuffer
                }
                finally
                {
                    D3D.SwapDeviceContextState(ctx1, previous, null);
                }
                Executions++;
                return true;
            }
            catch (Exception e)
            {
                Error = "execute " + e.GetType().Name + ": " + e.Message;
                _failed = true;
                return false;
            }
            finally
            {
                D3D.SafeRelease(ref previous);
                D3D.SafeRelease(ref ctx1);
            }
        }

        private void ReleaseObjects()
        {
            D3D.SafeRelease(ref _state);
            D3D.SafeRelease(ref _dss);
            D3D.SafeRelease(ref _rs);
            D3D.SafeRelease(ref _blend);
            D3D.SafeRelease(ref _ps);
            D3D.SafeRelease(ref _vs);
        }

        private void ReleaseAll()
        {
            ReleaseObjects();
            _device = IntPtr.Zero;
            _failed = false;
        }

        public void Dispose() { ReleaseAll(); }
    }

    /// <summary>
 /// S7t T1 (ROADMAP "### S7t Seamless transition" T1; CONTRACT §5.4 "S7t crossfade"): the crossfade pass of design F. One
 /// full-screen triangle on SE's backbuffer after SE's scene and before its HUD sprites (the <c>ConsumeMainSprites</c>
 /// prefix), blend on: colour = dest × factor (SrcBlend ZERO, DestBlend BLEND_FACTOR), alpha = factor.a (SrcBlendAlpha
 /// BLEND_FACTOR with the pixel shader's alpha 1, DestBlendAlpha ZERO), so the stored pixel becomes (c × f, a): with f
 /// from <see cref="CrossfadeRules.ColourFactor"/> the colour is premultiplied by the alpha DWM composes with (an sRGB
 /// target blends linear values: f = a^2.2). The HUD sprites draw afterwards with their own blending, over the faded
 /// scene as they draw over the cleared backbuffer of the scene skip. Same isolation as <see cref="AlphaFillPass"/>: its
 /// own <c>ID3DDeviceContextState</c>, swapped out and back; no reference to the backbuffer kept. Render thread only.
 /// </summary>
    internal sealed unsafe class CrossfadePass : IDisposable
    {
        public const string PixelShader =
            "float4 main() : SV_Target\n" +
            "{\n" +
            "    return float4(1.0, 1.0, 1.0, 1.0);\n" +
            "}\n";

        private IntPtr _device, _vs, _ps, _blend, _rs, _dss, _state;
        private bool _failed;

        public string Error { get; private set; }
        public long Executions { get; private set; }
        public bool Ready { get { return _state != IntPtr.Zero && !_failed; } }

        public bool Ensure(IntPtr device)
        {
            if (device == IntPtr.Zero) { Error = "no device"; return false; }
            if (device == _device) return Ready;
            ReleaseAll();
            _device = device;
            _failed = true;
            IntPtr device1 = IntPtr.Zero, vsBlob = IntPtr.Zero, psBlob = IntPtr.Zero;
            try
            {
                string err;
                int hr = D3D.Compile(AlphaFillPass.VertexShader, "main", "vs_4_0", out vsBlob, out err);
                if (hr < 0) { Error = "vertex shader compile " + NativeWin.Hr(hr) + " " + err; return false; }
                hr = D3D.Compile(PixelShader, "main", "ps_4_0", out psBlob, out err);
                if (hr < 0) { Error = "pixel shader compile " + NativeWin.Hr(hr) + " " + err; return false; }
                hr = D3D.CreateVertexShader(device, D3D.BlobPointer(vsBlob), D3D.BlobSize(vsBlob), out _vs);
                if (hr < 0) { Error = "CreateVertexShader " + NativeWin.Hr(hr); return false; }
                hr = D3D.CreatePixelShader(device, D3D.BlobPointer(psBlob), D3D.BlobSize(psBlob), out _ps);
                if (hr < 0) { Error = "CreatePixelShader " + NativeWin.Hr(hr); return false; }

                var bd = new D3D.BlendDesc();
                var rt = new D3D.RenderTargetBlendDesc
                {
                    BlendEnable = 1, SrcBlend = D3D.BlendZero, DestBlend = D3D.BlendBlendFactor, BlendOp = D3D.BlendOpAdd,
                    SrcBlendAlpha = D3D.BlendBlendFactor, DestBlendAlpha = D3D.BlendZero, BlendOpAlpha = D3D.BlendOpAdd,
                    RenderTargetWriteMask = D3D.ColorWriteAll,
                };
                var off = new D3D.RenderTargetBlendDesc
                {
                    BlendEnable = 0, SrcBlend = D3D.BlendOne, DestBlend = D3D.BlendZero, BlendOp = D3D.BlendOpAdd,
                    SrcBlendAlpha = D3D.BlendOne, DestBlendAlpha = D3D.BlendZero, BlendOpAlpha = D3D.BlendOpAdd,
                    RenderTargetWriteMask = D3D.ColorWriteAll,
                };
                bd.Rt0 = rt; bd.Rt1 = off; bd.Rt2 = off; bd.Rt3 = off; bd.Rt4 = off; bd.Rt5 = off; bd.Rt6 = off; bd.Rt7 = off;
                hr = D3D.CreateBlendState(device, ref bd, out _blend);
                if (hr < 0) { Error = "CreateBlendState " + NativeWin.Hr(hr); return false; }

                var rd = new D3D.RasterizerDesc { FillMode = D3D.FillSolid, CullMode = D3D.CullNone, DepthClipEnable = 1 };
                hr = D3D.CreateRasterizerState(device, ref rd, out _rs);
                if (hr < 0) { Error = "CreateRasterizerState " + NativeWin.Hr(hr); return false; }

                var op = new D3D.DepthStencilOpDesc { StencilFailOp = D3D.StencilOpKeep, StencilDepthFailOp = D3D.StencilOpKeep, StencilPassOp = D3D.StencilOpKeep, StencilFunc = D3D.ComparisonAlways };
                var dd = new D3D.DepthStencilDesc { DepthEnable = 0, DepthWriteMask = 0, DepthFunc = D3D.ComparisonAlways, StencilEnable = 0, StencilReadMask = 0xFF, StencilWriteMask = 0xFF, FrontFace = op, BackFace = op };
                hr = D3D.CreateDepthStencilState(device, ref dd, out _dss);
                if (hr < 0) { Error = "CreateDepthStencilState " + NativeWin.Hr(hr); return false; }

                hr = D3D.QueryInterface(device, D3D.IidDevice1, out device1);
                if (hr < 0 || device1 == IntPtr.Zero) { Error = "device is not ID3D11Device1 " + NativeWin.Hr(hr); return false; }
                hr = D3D.CreateDeviceContextState(device1, D3D.GetFeatureLevel(device), D3D.IidDevice1, out _state);
                if (hr < 0) { Error = "CreateDeviceContextState " + NativeWin.Hr(hr); return false; }

                _failed = false;
                Error = null;
                return true;
            }
            catch (Exception e)
            {
                Error = "setup " + e.GetType().Name + ": " + e.Message;
                return false;
            }
            finally
            {
                D3D.SafeRelease(ref device1);
                D3D.SafeRelease(ref vsBlob);
                D3D.SafeRelease(ref psBlob);
                if (_failed) ReleaseObjects();
            }
        }

        /// <summary>
 /// Scales the colour of <paramref name="rtv"/> (width × height) by <paramref name="colourFactor"/> and sets its alpha
 /// to <paramref name="alpha"/> on <paramref name="context"/>, leaving the caller's pipeline state unchanged.
 /// </summary>
        public bool Execute(IntPtr device, IntPtr context, IntPtr rtv, uint width, uint height, float colourFactor, float alpha)
        {
            if (context == IntPtr.Zero || rtv == IntPtr.Zero || width == 0 || height == 0) { Error = "no context, view or size"; return false; }
            if (!Ensure(device)) return false;
            IntPtr ctx1 = IntPtr.Zero, previous = IntPtr.Zero;
            try
            {
                int hr = D3D.QueryInterface(context, D3D.IidDeviceContext1, out ctx1);
                if (hr < 0 || ctx1 == IntPtr.Zero) { Error = "context is not ID3D11DeviceContext1 " + NativeWin.Hr(hr); _failed = true; return false; }
                float f = colourFactor < 0f ? 0f : (colourFactor > 1f ? 1f : colourFactor);
                float a = alpha < 0f ? 0f : (alpha > 1f ? 1f : alpha);
                D3D.SwapDeviceContextState(ctx1, _state, &previous);
                try
                {
                    D3D.IASetPrimitiveTopology(ctx1, D3D.TopologyTriangleList);
                    D3D.IASetInputLayout(ctx1, IntPtr.Zero);
                    D3D.VSSetShader(ctx1, _vs);
                    D3D.PSSetShader(ctx1, _ps);
                    D3D.RSSetState(ctx1, _rs);
                    D3D.RSSetViewport(ctx1, new D3D.Viewport { Width = width, Height = height, MaxDepth = 1f });
                    D3D.OMSetBlendState(ctx1, _blend, f, f, f, a);
                    D3D.OMSetDepthStencilState(ctx1, _dss);
                    D3D.OMSetRenderTarget(ctx1, rtv);
                    D3D.Draw(ctx1, 3);
                    D3D.OMSetRenderTarget(ctx1, IntPtr.Zero); // our state object keeps no reference to the backbuffer
                }
                finally
                {
                    D3D.SwapDeviceContextState(ctx1, previous, null);
                }
                Executions++;
                return true;
            }
            catch (Exception e)
            {
                Error = "execute " + e.GetType().Name + ": " + e.Message;
                _failed = true;
                return false;
            }
            finally
            {
                D3D.SafeRelease(ref previous);
                D3D.SafeRelease(ref ctx1);
            }
        }

        private void ReleaseObjects()
        {
            D3D.SafeRelease(ref _state);
            D3D.SafeRelease(ref _dss);
            D3D.SafeRelease(ref _rs);
            D3D.SafeRelease(ref _blend);
            D3D.SafeRelease(ref _ps);
            D3D.SafeRelease(ref _vs);
        }

        private void ReleaseAll()
        {
            ReleaseObjects();
            _device = IntPtr.Zero;
            _failed = false;
        }

        public void Dispose() { ReleaseAll(); }
    }

    /// <summary>Alpha statistics of one backbuffer readback (S2.T-D1: "&lt; 2 % alpha &gt; 0 with no menu"; "&lt; 0.1 % alpha &lt; 1 with the scene").</summary>
    internal struct AlphaStats
    {
        public uint Width, Height;
        public int Format;
        public long Pixels, AlphaGt0, AlphaLt1, Alpha0;

        public double FractionGt0 { get { return Pixels > 0 ? (double)AlphaGt0 / Pixels : 0; } }
        public double FractionLt1 { get { return Pixels > 0 ? (double)AlphaLt1 / Pixels : 0; } }
    }

    /// <summary>
 /// Dev-only backbuffer alpha readback (ROADMAP S2 "Backbuffer alpha readback (dev only)"): the backbuffer is copied
 /// to a staging texture after the frame is complete (sprites and any alpha fill), mapped a few frames later with
 /// <c>DO_NOT_WAIT</c> (no render-thread stall while the GPU is busy; a blocking map after 30 frames), every pixel's
 /// alpha byte counted, and the staging texture released again (about 15 MB at 2560×1440). Render thread only.
 /// </summary>
    internal sealed unsafe class AlphaReadback : IDisposable
    {
        public const int MaxWaitFrames = 30;

        private IntPtr _staging;
        private Texture2DDescCopy _desc;
        private bool _pending;
        private int _waited;

        private struct Texture2DDescCopy { public uint Width, Height; public int Format; }

        public bool Pending { get { return _pending; } }
        public string Error { get; private set; }

        /// <summary>Copies <paramref name="texture"/> (any ID3D11Resource of an 8-bit RGBA/BGRA single-sample 2D texture).</summary>
        public bool BeginCopy(IntPtr device, IntPtr context, IntPtr texture)
        {
            Error = null;
            if (_pending) return true;
            IntPtr tex = IntPtr.Zero;
            try
            {
                int hr = D3D.QueryInterface(texture, D3D.IidTexture2D, out tex);
                if (hr < 0 || tex == IntPtr.Zero) { Error = "resource is not a Texture2D " + NativeWin.Hr(hr); return false; }
                D3D.Texture2DDesc d;
                D3D.GetDesc(tex, out d);
                if (d.SampleCount != 1) { Error = "multisampled backbuffer (" + d.SampleCount + " samples)"; return false; }
                if (D3D.AlphaByte(d.Format) < 0) { Error = "format " + d.Format + " not supported"; return false; }
                var s = new D3D.Texture2DDesc
                {
                    Width = d.Width, Height = d.Height, MipLevels = 1, ArraySize = 1, Format = d.Format, SampleCount = 1,
                    Usage = D3D.UsageStaging, CPUAccessFlags = D3D.CpuAccessRead,
                };
                hr = D3D.CreateTexture2D(device, ref s, out _staging);
                if (hr < 0 || _staging == IntPtr.Zero) { Error = "CreateTexture2D (staging) " + NativeWin.Hr(hr); return false; }
                D3D.CopyResource(context, _staging, tex);
                _desc = new Texture2DDescCopy { Width = d.Width, Height = d.Height, Format = d.Format };
                _pending = true;
                _waited = 0;
                return true;
            }
            catch (Exception e)
            {
                Error = "copy " + e.GetType().Name + ": " + e.Message;
                D3D.SafeRelease(ref _staging);
                return false;
            }
            finally { D3D.SafeRelease(ref tex); }
        }

        /// <summary>
 /// Maps the staging copy when the GPU is done; true with <paramref name="stats"/> once counted, false while still
 /// pending or on failure (<see cref="Error"/> set, nothing pending any more).
 /// </summary>
        public bool TryFinish(IntPtr context, out AlphaStats stats)
        {
            stats = default(AlphaStats);
            if (!_pending) return false;
            try
            {
                D3D.MappedSubresource m;
                int hr = D3D.Map(context, _staging, D3D.MapRead, D3D.MapFlagDoNotWait, out m);
                if (hr == D3D.DxgiErrorWasStillDrawing)
                {
                    if (++_waited <= MaxWaitFrames) return false;
                    hr = D3D.Map(context, _staging, D3D.MapRead, 0u, out m);
                }
                if (hr < 0) { Error = "Map " + NativeWin.Hr(hr); Abandon(); return false; }
                try
                {
                    stats.Width = _desc.Width;
                    stats.Height = _desc.Height;
                    stats.Format = _desc.Format;
                    Count((byte*)m.Data, m.RowPitch, _desc.Width, _desc.Height, D3D.AlphaByte(_desc.Format), ref stats);
                }
                finally { D3D.Unmap(context, _staging); }
                Abandon();
                return true;
            }
            catch (Exception e)
            {
                Error = "map " + e.GetType().Name + ": " + e.Message;
                Abandon();
                return false;
            }
        }

        /// <summary>Counts the alpha bytes of a mapped 4-byte-per-pixel image.</summary>
        public static void Count(byte* data, uint rowPitch, uint width, uint height, int alphaByte, ref AlphaStats s)
        {
            long gt0 = 0, lt1 = 0, zero = 0;
            for (uint y = 0; y < height; y++)
            {
                byte* p = data + (long)y * rowPitch + alphaByte;
                for (uint x = 0; x < width; x++, p += 4)
                {
                    byte a = *p;
                    if (a != 0) gt0++; else zero++;
                    if (a != 255) lt1++;
                }
            }
            s.Pixels = (long)width * height;
            s.AlphaGt0 = gt0;
            s.AlphaLt1 = lt1;
            s.Alpha0 = zero;
        }

        private void Abandon()
        {
            _pending = false;
            _waited = 0;
            D3D.SafeRelease(ref _staging);
        }

        public void Dispose() { Abandon(); }
    }
}
