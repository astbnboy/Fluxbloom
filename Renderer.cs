using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using ShaderResourceViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension;
using Viewport = Vortice.Mathematics.Viewport;

namespace Fluxbloom;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
unsafe struct FrameCB
{
    public float ResX, ResY, InvResX, InvResY;
    public float Aspect, Time, Dt; public uint Frame;
    public float Exposure, Bloom, Decay, Inject;
    public uint ParticleCount, OriginCount, GamutMode, OutMode;
    public float PaperWhite, Hud, FlowSpeed, Dither;
    public float Haze, Detail, ParticleSize, Brightness;
    public float Advect, SoftFrac, Pad0, Pad1;
    public float SeedX, SeedY, SeedZ, SeedW;
    public float FlowTexW, FlowTexH, HudW, HudH;
    public fixed float Origins[Director.Max * Director.FloatsPerOrigin];
}

public struct RenderStats
{
    public double Fps, CpuMs, GpuMs, GpuFieldMs, GpuPostMs, GpuSimMs, GpuFeedbackMs, GpuParticlesMs;
    public int RenderW, RenderH, OutW, OutH, Particles, Forms;
    public float Scale, ParticleFraction;
    public long VramUsed, VramBudget;
    public string Gpu, Mode;
    public float Hz; public int Cap;
}

/// <summary>Direct3D 12 renderer: compute flow field + particle simulation, feedback field, oriented-splat particles, bloom, composite/encode.</summary>
public sealed unsafe class Renderer : IDisposable
{
    const int FrameCount = 3;
    const int TableStride = 8;
    const Format SceneFmt = Format.R16G16B16A16_Float;
    const Format FinalFmt = Format.R10G10B10A2_UNorm;
    const int ParticleBytes = 64;
    const int Ts = 6; // timestamps per frame
    const int HudW = 512, HudH = 176;
    const uint AllSub = 0xFFFFFFFF;

    sealed class Tex
    {
        public ID3D12Resource Res = null!;
        public ResourceStates State;
        public int W, H;
        public CpuDescriptorHandle Rtv;
    }

    readonly Settings set;
    readonly Director dir;
    readonly bool offscreen;
    readonly string name;

    IDXGIFactory4 factory = null!;
    IDXGIAdapter1 adapter = null!;
    IDXGIAdapter3? adapter3;
    ID3D12Device device = null!;
    ID3D12CommandQueue queue = null!;
    IDXGISwapChain3? swap;
    ID3D12CommandAllocator[] allocs = new ID3D12CommandAllocator[FrameCount];
    ID3D12GraphicsCommandList cmd = null!;
    ID3D12Fence fence = null!;
    ulong fenceValue;
    ulong[] frameFence = new ulong[FrameCount];
    EventWaitHandle fenceEvent = new(false, EventResetMode.AutoReset);

    ID3D12DescriptorHeap rtvHeap = null!, srvHeap = null!;
    uint rtvInc, srvInc;
    CpuDescriptorHandle rtvBase;
    int rtvNext;
    ID3D12RootSignature rootSig = null!;
    ID3D12PipelineState psoField = null!, psoParticles = null!, psoDown = null!, psoUp = null!, psoComposite = null!, psoUpdate = null!, psoFlow = null!;

    ID3D12Resource[] cbUpload = new ID3D12Resource[FrameCount];
    byte*[] cbPtr = new byte*[FrameCount];
    ID3D12Resource particles = null!;
    ResourceStates particleState = ResourceStates.UnorderedAccess;
    Tex flow = null!;
    Tex[] scene = new Tex[2];
    Tex[] bloom = new Tex[4];
    ID3D12Resource? hudTex, hudUpload;
    byte* hudPtr;
    ResourceStates hudState = ResourceStates.PixelShaderResource;
    bool hudDirty;
    ID3D12Resource[] backBuffers = new ID3D12Resource[FrameCount];
    CpuDescriptorHandle[] backRtv = new CpuDescriptorHandle[FrameCount];
    ID3D12Resource? offFinal; CpuDescriptorHandle offRtv;
    ResourceStates offState = ResourceStates.PixelShaderResource;

    ID3D12QueryHeap queryHeap = null!;
    ID3D12Resource queryReadback = null!;
    ulong* queryPtr;
    double tsFreq;
    bool captureNext;
    ID3D12Resource? captureBuf;

    // table indices (each table = TableStride descriptors)
    const int T_UPDATE = 0, T_FLOW = 1, T_FIELD = 2 /*2*/, T_DOWN0 = 4 /*2*/, T_DOWN = 6 /*3: src L0,L1,L2*/, T_UP = 9 /*3: src L3,L2,L1*/, T_COMP = 12 /*2*/, TableCount = 14;

    public int OutW { get; private set; }
    public int OutH { get; private set; }
    int rw, rh;
    float scale = 1f, minScale = 0.5f, maxScale = 1f;
    float particleFrac = 1f;
    int capacity, activeBase;
    int qEff;
    int cur;
    uint frameIndex;
    int syncInterval = 1;
    bool tearing;
    bool hdrOut;
    public float RefreshHz = 60f;
    string gpuName = "";
    string shaderSource = "";

    // stats
    readonly Stopwatch clock = Stopwatch.StartNew();
    double lastFrameT;
    double fpsAcc; int fpsCount; double fpsShown;
    double cpuMsAcc;
    double gpuTotal, gpuSim, gpuField, gpuPost, gpuFieldOnly, gpuParts;
    double govTimer, lastChangeT;
    int govFrames; double govGpuSum;
    double nextFrameDeadline;
    double hudTimer;
    public RenderStats Stats;

    public bool ManualPacing { get; }
    public Renderer(IntPtr hwnd, int outW, int outH, Settings s, Director d, bool offscreen, float refreshHz, string tag = "main", bool manualPacing = false)
    {
        set = s; dir = d; this.offscreen = offscreen; name = tag; OutW = outW; OutH = outH; RefreshHz = refreshHz; ManualPacing = manualPacing;
        qEff = Math.Clamp(s.Quality, 0, 2);
        try
        {
            shaderSource = LoadShaderSource();
            CreateDevice();
            qEff = FitQualityToVram(qEff);
            CreateQueueAndSync();
            if (!offscreen) CreateSwapChain(hwnd);
            CreateHeaps();
            CreateRootSignatureAndPipelines();
            CreateStaticResources();
            if (offscreen) CreateOffscreenTarget();
            else CreateBackBufferViews();
            PickInitialScale();
            CreateSizeDependent();
            ConfigurePacing();
        }
        catch { try { Dispose(); } catch { } throw; }
    }

    /// <summary>Builds a renderer, stepping down one quality tier if GPU memory or resource creation fails.</summary>
    public static Renderer CreateWithFallback(IntPtr hwnd, int w, int h, Settings s, Director d, bool offscreen, float hz, string tag, bool manualPacing)
    {
        var cfg = s;
        while (true)
        {
            try { return new Renderer(hwnd, w, h, cfg, d, offscreen, hz, tag, manualPacing); }
            catch (Exception ex) when (cfg.Quality > 0 && (ex is SharpGenException || ex is OutOfMemoryException))
            {
                RunLog.Line($"quality {cfg.Quality} failed ({ex.GetType().Name}: {ex.Message}); retrying at quality {cfg.Quality - 1}");
                cfg = cfg.Clone(); cfg.Quality--;
            }
        }
    }

    long EstimateBytes(int q)
    {
        int[] baseP = { 700_000, 1_600_000, 3_200_000 };
        float[] caps = { 0.6f, 0.85f, 1f };
        double sc = OutW < 1800 ? 1.0 : caps[q];
        double px = OutW * sc * OutH * sc;
        return (long)(baseP[q] * 1.5 * ParticleBytes + px * 8 * 2.4 + (double)OutW * OutH * 4 * 3);
    }

    int FitQualityToVram(int q)
    {
        long budget = 0;
        try { budget = (long)(adapter3?.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local).Budget ?? 0); } catch { }
        { var fake = Environment.GetEnvironmentVariable("FLX_VRAM_BUDGET_MB"); if (long.TryParse(fake, out var mb)) budget = mb * 1048576; } // test hook
        if (budget <= 0) return q;
        while (q > 0 && EstimateBytes(q) > budget / 2) { RunLog.Line($"VRAM budget {budget / 1048576} MB too small for quality {q} (needs about {EstimateBytes(q) / 1048576} MB); lowering"); q--; }
        return q;
    }

    // ------------------------------------------------------------------ setup
    static string LoadShaderSource()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var st = asm.GetManifestResourceStream("Fluxbloom.Shaders.Fluxbloom.hlsl") ?? throw new InvalidOperationException("shader resource missing");
        using var rd = new StreamReader(st);
        return rd.ReadToEnd();
    }

    void CreateDevice()
    {
        factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(false);
        IDXGIAdapter1? best = null; ulong bestMem = 0;
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 a).Success; i++)
        {
            var desc = a.Description1;
            if ((desc.Flags & AdapterFlags.Software) != 0) { a.Dispose(); continue; }
            if (desc.DedicatedVideoMemory >= bestMem) { best?.Dispose(); best = a; bestMem = desc.DedicatedVideoMemory; }
            else a.Dispose();
        }
        adapter = best ?? throw new InvalidOperationException("No hardware GPU adapter found");
        gpuName = adapter.Description1.Description;
        adapter3 = adapter.QueryInterfaceOrNull<IDXGIAdapter3>();
        device = D3D12.D3D12CreateDevice<ID3D12Device>(adapter, FeatureLevel.Level_11_0);
    }

    void CreateQueueAndSync()
    {
        queue = device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
        for (int i = 0; i < FrameCount; i++) allocs[i] = device.CreateCommandAllocator(CommandListType.Direct);
        cmd = device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, allocs[0], null);
        cmd.Close();
        fence = device.CreateFence(0, FenceFlags.None);
        { ulong fq = 0; queue.GetTimestampFrequency(out fq); tsFreq = fq; }
    }

    void CreateSwapChain(IntPtr hwnd)
    {
        var f5 = factory.QueryInterfaceOrNull<IDXGIFactory5>();
        if (f5 != null) { try { tearing = f5.PresentAllowTearing; } catch { tearing = false; } f5.Dispose(); }
        var desc = new SwapChainDescription1(
            (uint)OutW, (uint)OutH, FinalFmt, false, Usage.RenderTargetOutput, FrameCount, Scaling.None,
            SwapEffect.FlipDiscard, AlphaMode.Ignore, tearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None);
        using var sc1 = factory.CreateSwapChainForHwnd(queue, hwnd, desc, null, null);
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter | WindowAssociationFlags.IgnoreAll);
        swap = sc1.QueryInterface<IDXGISwapChain3>();
        hdrOut = false;
        if (set.WideGamutHdr) TryEnableHdr(hwnd);
    }

    void TryEnableHdr(IntPtr hwnd)
    {
        try
        {
            IntPtr mon = NativeMethods.MonitorFromWindow(hwnd, 2);
            for (uint i = 0; adapter.EnumOutputs(i, out IDXGIOutput o).Success; i++)
            {
                using (o)
                {
                    if (o.Description.Monitor != mon) continue;
                    using var o6 = o.QueryInterfaceOrNull<IDXGIOutput6>();
                    if (o6 == null) return;
                    var d1 = o6.Description1;
                    if (d1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020 &&
                        (swap!.CheckColorSpaceSupport(ColorSpaceType.RgbFullG2084NoneP2020) & SwapChainColorSpaceSupportFlags.Present) != 0)
                    {
                        swap.SetColorSpace1(ColorSpaceType.RgbFullG2084NoneP2020);
                        hdrOut = true;
                    }
                    return;
                }
            }
        }
        catch { hdrOut = false; }
    }

    void CreateHeaps()
    {
        rtvHeap = device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 24, DescriptorHeapFlags.None, 0));
        srvHeap = device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, TableCount * TableStride, DescriptorHeapFlags.ShaderVisible, 0));
        rtvInc = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        srvInc = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        rtvBase = rtvHeap.GetCPUDescriptorHandleForHeapStart();
    }

    CpuDescriptorHandle NextRtv() { var h = rtvBase; h.Ptr += (nuint)(rtvNext * rtvInc); rtvNext++; return h; }
    CpuDescriptorHandle SrvCpu(int table, int slot) { var h = srvHeap.GetCPUDescriptorHandleForHeapStart(); h.Ptr += (nuint)((table * TableStride + slot) * srvInc); return h; }
    GpuDescriptorHandle SrvGpu(int table) { var h = srvHeap.GetGPUDescriptorHandleForHeapStart(); h.Ptr += (ulong)(table * TableStride * srvInc); return h; }

    byte[] Compile(string entry, string profile)
    {
        var r = Compiler.Compile(shaderSource, null!, null!, entry, "Fluxbloom.hlsl", profile, ShaderFlags.OptimizationLevel3, out Blob code, out Blob err);
        if (r.Failure) throw new InvalidOperationException($"Shader {entry} failed: {err?.AsString()}");
        byte[] bytes = code.AsBytes();
        code.Dispose(); err?.Dispose();
        return bytes;
    }

    void CreateRootSignatureAndPipelines()
    {
        var ranges = new[]
        {
            new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 5, 0, 0, 0, DescriptorRangeFlags.DescriptorsVolatile | DescriptorRangeFlags.DataVolatile),
            new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 2, 0, 0, 5, DescriptorRangeFlags.DescriptorsVolatile | DescriptorRangeFlags.DataVolatile),
        };
        var parms = new[]
        {
            new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0, RootDescriptorFlags.None), ShaderVisibility.All),
            new RootParameter1(new RootDescriptorTable1(ranges), ShaderVisibility.All),
            new RootParameter1(new RootConstants(1, 0, 4), ShaderVisibility.All),
        };
        var samplers = new[]
        {
            new StaticSamplerDescription(0, Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp,
                0f, 1, ComparisonFunction.Never, StaticBorderColor.OpaqueBlack, 0f, float.MaxValue, ShaderVisibility.All, 0)
        };
        rootSig = device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None, parms, samplers));

        var vsFull = Compile("VS_Full", "vs_5_1");
        GraphicsPipelineStateDescription Gfx(byte[] vs, byte[] ps, BlendDescription blend, Format fmt) => new()
        {
            RootSignature = rootSig, VertexShader = vs, PixelShader = ps, BlendState = blend,
            RasterizerState = RasterizerDescription.CullNone, DepthStencilState = DepthStencilDescription.None,
            SampleMask = uint.MaxValue, PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[] { fmt }, SampleDescription = new SampleDescription(1, 0), InputLayout = new InputLayoutDescription(),
        };
        var add = new BlendDescription(Blend.One, Blend.One);
        psoField = device.CreateGraphicsPipelineState(Gfx(vsFull, Compile("PS_Field", "ps_5_1"), BlendDescription.Opaque, SceneFmt));
        psoParticles = device.CreateGraphicsPipelineState(Gfx(Compile("VS_Particle", "vs_5_1"), Compile("PS_Particle", "ps_5_1"), add, SceneFmt));
        psoDown = device.CreateGraphicsPipelineState(Gfx(vsFull, Compile("PS_Down", "ps_5_1"), BlendDescription.Opaque, SceneFmt));
        psoUp = device.CreateGraphicsPipelineState(Gfx(vsFull, Compile("PS_Up", "ps_5_1"), add, SceneFmt));
        psoComposite = device.CreateGraphicsPipelineState(Gfx(vsFull, Compile("PS_Composite", "ps_5_1"), BlendDescription.Opaque, FinalFmt));
        psoUpdate = device.CreateComputePipelineState(new ComputePipelineStateDescription { RootSignature = rootSig, ComputeShader = Compile("CS_Update", "cs_5_1") });
        psoFlow = device.CreateComputePipelineState(new ComputePipelineStateDescription { RootSignature = rootSig, ComputeShader = Compile("CS_Flow", "cs_5_1") });
    }

    ID3D12Resource CommittedBuffer(HeapType type, ulong size, ResourceFlags flags, ResourceStates state)
        => device.CreateCommittedResource(type, HeapFlags.None, ResourceDescription.Buffer(size, flags, 0), state, null);

    void CreateStaticResources()
    {
        for (int i = 0; i < FrameCount; i++)
        {
            cbUpload[i] = CommittedBuffer(HeapType.Upload, 8192, ResourceFlags.None, ResourceStates.GenericRead);
            cbPtr[i] = cbUpload[i].Map<byte>(0);
        }
        queryHeap = device.CreateQueryHeap<ID3D12QueryHeap>(new QueryHeapDescription(QueryHeapType.Timestamp, (uint)(Ts * FrameCount), 0));
        queryReadback = CommittedBuffer(HeapType.Readback, (ulong)(Ts * FrameCount * 8), ResourceFlags.None, ResourceStates.CopyDest);
        queryPtr = queryReadback.Map<ulong>(0);

        // HUD texture (diagnostics), hidden unless enabled
        hudTex = device.CreateCommittedResource(HeapType.Default, HeapFlags.None,
            ResourceDescription.Texture2D(Format.B8G8R8A8_UNorm, HudW, HudH, 1, 1, 1, 0, ResourceFlags.None, TextureLayout.Unknown, 0),
            ResourceStates.PixelShaderResource, null);
        hudUpload = CommittedBuffer(HeapType.Upload, (ulong)(HudW * 4 * HudH), ResourceFlags.None, ResourceStates.GenericRead);
        hudPtr = hudUpload.Map<byte>(0);

        // Particle buffer sized by quality
        int[] baseP = { 700_000, 1_600_000, 3_200_000 };
        activeBase = baseP[qEff];
        capacity = (int)(activeBase * 1.5f);
        particles = CommittedBuffer(HeapType.Default, (ulong)capacity * ParticleBytes, ResourceFlags.AllowUnorderedAccess, ResourceStates.UnorderedAccess);
        particleState = ResourceStates.UnorderedAccess;
    }

    void CreateBackBufferViews()
    {
        for (int i = 0; i < FrameCount; i++)
        {
            backBuffers[i] = swap!.GetBuffer<ID3D12Resource>((uint)i);
            backRtv[i] = NextRtv();
            device.CreateRenderTargetView(backBuffers[i], null, backRtv[i]);
        }
    }

    void CreateOffscreenTarget()
    {
        offFinal = device.CreateCommittedResource(HeapType.Default, HeapFlags.None,
            ResourceDescription.Texture2D(FinalFmt, (uint)OutW, (uint)OutH, 1, 1, 1, 0, ResourceFlags.AllowRenderTarget, TextureLayout.Unknown, 0),
            ResourceStates.PixelShaderResource, null);
        offRtv = NextRtv();
        device.CreateRenderTargetView(offFinal, null, offRtv);
    }

    void PickInitialScale()
    {
        float[] caps = { 0.6f, 0.85f, 1f };
        maxScale = caps[qEff];
        minScale = 0.45f;
        scale = maxScale;
        if (OutW < 1800) { scale = 1f; maxScale = 1f; } // small previews render natively
    }

    Tex MakeTarget(Format fmt, int w, int h, bool createRtv)
    {
        var t = new Tex { W = w, H = h, State = ResourceStates.PixelShaderResource };
        t.Res = device.CreateCommittedResource(HeapType.Default, HeapFlags.None,
            ResourceDescription.Texture2D(fmt, (uint)w, (uint)h, 1, 1, 1, 0, ResourceFlags.AllowRenderTarget, TextureLayout.Unknown, 0),
            ResourceStates.PixelShaderResource, null);
        if (createRtv) { t.Rtv = NextRtv(); device.CreateRenderTargetView(t.Res, null, t.Rtv); }
        return t;
    }

    int sizeRtvMark = -1;
    void CreateSizeDependent()
    {
        if (sizeRtvMark < 0) sizeRtvMark = rtvNext; else rtvNext = sizeRtvMark;
        rw = Math.Max(64, ((int)(OutW * scale)) & ~1);
        rh = Math.Max(64, ((int)(OutH * scale)) & ~1);
        for (int i = 0; i < 2; i++) { scene[i]?.Res.Dispose(); scene[i] = MakeTarget(SceneFmt, rw, rh, true); }
        for (int i = 0; i < 4; i++) { bloom[i]?.Res.Dispose(); bloom[i] = MakeTarget(SceneFmt, Math.Max(2, rw >> (i + 1)), Math.Max(2, rh >> (i + 1)), true); }
        int fw = (rw + 7) / 8, fh = (rh + 7) / 8;
        flow?.Res.Dispose();
        flow = new Tex { W = fw, H = fh, State = ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource };
        flow.Res = device.CreateCommittedResource(HeapType.Default, HeapFlags.None,
            ResourceDescription.Texture2D(Format.R16G16_Float, (uint)fw, (uint)fh, 1, 1, 1, 0, ResourceFlags.AllowUnorderedAccess, TextureLayout.Unknown, 0),
            flow.State, null);
        WriteTables();
        cur = 0;
    }

    void SrvTex(int table, int slot, ID3D12Resource r, Format fmt)
    {
        var d = new ShaderResourceViewDescription
        {
            Format = fmt, ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        };
        device.CreateShaderResourceView(r, d, SrvCpu(table, slot));
    }

    void SrvParticles(int table, int slot)
    {
        var d = new ShaderResourceViewDescription
        {
            Format = Format.Unknown, ViewDimension = ShaderResourceViewDimension.Buffer,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)capacity, StructureByteStride = ParticleBytes, Flags = BufferShaderResourceViewFlags.None },
        };
        device.CreateShaderResourceView(particles, d, SrvCpu(table, slot));
    }

    void WriteTables()
    {
        // slots: 0 t0 tex0, 1 t1 tex1, 2 t2 particles(SRV), 3 t3 hud, 4 t4 flow(SRV), 5 u0 particles(UAV), 6 u1 flow(UAV)
        void Common(int t)
        {
            SrvParticles(t, 2);
            SrvTex(t, 3, hudTex!, Format.B8G8R8A8_UNorm);
            SrvTex(t, 4, flow.Res, Format.R16G16_Float);
            var u0 = new UnorderedAccessViewDescription
            {
                Format = Format.Unknown, ViewDimension = UnorderedAccessViewDimension.Buffer,
                Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)capacity, StructureByteStride = ParticleBytes, Flags = BufferUnorderedAccessViewFlags.None },
            };
            device.CreateUnorderedAccessView(particles, null, u0, SrvCpu(t, 5));
            var u1 = new UnorderedAccessViewDescription { Format = Format.R16G16_Float, ViewDimension = UnorderedAccessViewDimension.Texture2D, Texture2D = new Texture2DUnorderedAccessView { MipSlice = 0, PlaneSlice = 0 } };
            device.CreateUnorderedAccessView(flow.Res, null, u1, SrvCpu(t, 6));
        }
        for (int t = 0; t < TableCount; t++) Common(t);
        for (int c = 0; c < 2; c++)
        {
            SrvTex(T_FIELD + c, 0, scene[1 - c].Res, SceneFmt);        // field reads previous scene
            SrvTex(T_DOWN0 + c, 0, scene[c].Res, SceneFmt);            // bloom reads the freshly drawn scene
            SrvTex(T_COMP + c, 0, scene[c].Res, SceneFmt);
            SrvTex(T_COMP + c, 1, bloom[0].Res, SceneFmt);
        }
        for (int i = 0; i < 3; i++)
        {
            SrvTex(T_DOWN + i, 0, bloom[i].Res, SceneFmt);              // down level i -> i+1
            SrvTex(T_UP + i, 0, bloom[3 - i].Res, SceneFmt);            // up level 3-i -> 2-i
        }
    }

    void ConfigurePacing()
    {
        float hz = Math.Max(24f, RefreshHz);
        int cap = set.FrameCap == 0 ? (int)MathF.Min(hz, 240f) : set.FrameCap;
        if (qEff == 0 && set.FrameCap == 0) cap = (int)MathF.Min(cap, 60f);
        Stats.Cap = cap; Stats.Hz = hz;
        if (set.VSync && !ManualPacing) syncInterval = Math.Max(1, (int)MathF.Round(hz / MathF.Max(cap, 1f)));
        else syncInterval = 0;
    }

    // ------------------------------------------------------------------ frame
    void Tr(ID3D12Resource r, ref ResourceStates cur, ResourceStates next)
    {
        if (cur == next) return;
        cmd.ResourceBarrierTransition(r, cur, next, AllSub, ResourceBarrierFlags.None);
        cur = next;
    }
    void Tr(Tex t, ResourceStates next) => Tr(t.Res, ref t.State, next);

    void WaitForFence(ulong v)
    {
        if (fence.CompletedValue >= v) return;
        fence.SetEventOnCompletion(v, fenceEvent);
        fenceEvent.WaitOne();
    }

    public void WaitIdle()
    {
        fenceValue++;
        queue.Signal(fence, fenceValue);
        WaitForFence(fenceValue);
    }

    void SetRoot(GpuDescriptorHandle table, ulong cbva, bool compute)
    {
        if (compute) { cmd.SetComputeRootConstantBufferView(0, cbva); cmd.SetComputeRootDescriptorTable(1, table); }
        else { cmd.SetGraphicsRootConstantBufferView(0, cbva); cmd.SetGraphicsRootDescriptorTable(1, table); }
    }

    void PassConst(float x, float y, float z, float w)
    {
        float* v = stackalloc float[4]; v[0] = x; v[1] = y; v[2] = z; v[3] = w;
        cmd.SetGraphicsRoot32BitConstants(2, 4, (IntPtr)v, 0);
    }

    void DrawFull(Tex target, bool clear = false)
    {
        cmd.OMSetRenderTargets(target.Rtv, null);
        cmd.RSSetViewport(new Viewport(0, 0, target.W, target.H, 0f, 1f));
        cmd.RSSetScissorRect(target.W, target.H);
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cmd.DrawInstanced(3, 1, 0, 0);
    }

    public void RequestCapture() => captureNext = true;

    public void RenderFrame(float dtIn)
    {
        double t0 = clock.Elapsed.TotalMilliseconds;
        float dt = Math.Clamp(dtIn, 1f / 400f, 1f / 15f);
        int fi = (int)(frameIndex % FrameCount);
        WaitForFence(frameFence[fi]);
        ReadTimestamps(fi);

        dir.Update(dt);
        UpdateHud(dt);

        allocs[fi].Reset();
        cmd.Reset(allocs[fi], null);
        cmd.EndQuery(queryHeap, QueryType.Timestamp, (uint)(fi * Ts + 0));
        cmd.SetDescriptorHeaps(1, new[] { srvHeap });

        // constants
        FrameCB cb = BuildConstants(dt);
        *(FrameCB*)cbPtr[fi] = cb;
        ulong cbva = cbUpload[fi].GPUVirtualAddress;

        // optional HUD upload
        if (hudDirty)
        {
            Tr(hudTex!, ref hudState, ResourceStates.CopyDest);
            var fp = new PlacedSubresourceFootPrint { Offset = 0, Footprint = new SubresourceFootPrint(Format.B8G8R8A8_UNorm, HudW, HudH, 1, HudW * 4) };
            cmd.CopyTextureRegion(new TextureCopyLocation(hudTex!, 0), 0, 0, 0, new TextureCopyLocation(hudUpload!, fp), null);
            Tr(hudTex!, ref hudState, ResourceStates.PixelShaderResource);
            hudDirty = false;
        }

        // ---- compute: flow field then particles
        Tr(flow.Res, ref flow.State, ResourceStates.UnorderedAccess);
        Tr(particles, ref particleState, ResourceStates.UnorderedAccess);
        cmd.SetComputeRootSignature(rootSig);
        cmd.SetPipelineState(psoFlow);
        SetRoot(SrvGpu(T_FLOW), cbva, true);
        cmd.Dispatch((uint)((flow.W + 7) / 8), (uint)((flow.H + 7) / 8), 1);
        Tr(flow.Res, ref flow.State, ResourceStates.NonPixelShaderResource | ResourceStates.PixelShaderResource);

        cmd.SetPipelineState(psoUpdate);
        SetRoot(SrvGpu(T_UPDATE), cbva, true);
        cmd.Dispatch((uint)((cb.ParticleCount + 63) / 64), 1, 1);
        Tr(particles, ref particleState, ResourceStates.NonPixelShaderResource | ResourceStates.PixelShaderResource);
        cmd.EndQuery(queryHeap, QueryType.Timestamp, (uint)(fi * Ts + 1));

        // ---- graphics: field (feedback) + particles into scene[cur]
        var dst = scene[cur];
        Tr(dst, ResourceStates.RenderTarget);
        cmd.SetGraphicsRootSignature(rootSig);
        cmd.SetPipelineState(psoField);
        SetRoot(SrvGpu(T_FIELD + cur), cbva, false);
        PassConst(0, 0, 0, 0);
        DrawFull(dst);
        cmd.EndQuery(queryHeap, QueryType.Timestamp, (uint)(fi * Ts + 2));
        cmd.SetPipelineState(psoParticles);
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        cmd.DrawInstanced(4, cb.ParticleCount, 0, 0);
        Tr(dst, ResourceStates.PixelShaderResource);
        cmd.EndQuery(queryHeap, QueryType.Timestamp, (uint)(fi * Ts + 3));

        // ---- bloom: down chain then additive up chain
        for (int i = 0; i < 4; i++)
        {
            var b = bloom[i];
            Tr(b, ResourceStates.RenderTarget);
            cmd.SetPipelineState(psoDown);
            SetRoot(SrvGpu(i == 0 ? T_DOWN0 + cur : T_DOWN + (i - 1)), cbva, false);
            var srcTex = i == 0 ? dst : bloom[i - 1];
            PassConst(1f / srcTex.W, 1f / srcTex.H, 1f, 0f);
            DrawFull(b);
            Tr(b, ResourceStates.PixelShaderResource);
        }
        for (int i = 0; i < 3; i++)
        {
            var src = bloom[3 - i]; var d2 = bloom[2 - i];
            Tr(d2, ResourceStates.RenderTarget);
            cmd.SetPipelineState(psoUp);
            SetRoot(SrvGpu(T_UP + i), cbva, false);
            PassConst(1f / src.W, 1f / src.H, 0.85f, 0f);
            DrawFull(d2);
            Tr(d2, ResourceStates.PixelShaderResource);
        }

        // ---- composite to back buffer (or offscreen target)
        CpuDescriptorHandle finalRtv; ID3D12Resource finalRes; int bi = 0;
        if (offscreen) { finalRtv = offRtv; finalRes = offFinal!; Tr(offFinal!, ref offState, ResourceStates.RenderTarget); }
        else
        {
            bi = (int)swap!.CurrentBackBufferIndex;
            finalRtv = backRtv[bi]; finalRes = backBuffers[bi];
            cmd.ResourceBarrierTransition(finalRes, ResourceStates.Present, ResourceStates.RenderTarget, AllSub, ResourceBarrierFlags.None);
        }
        cmd.SetPipelineState(psoComposite);
        SetRoot(SrvGpu(T_COMP + cur), cbva, false);
        PassConst(0, 0, 0, 0);
        cmd.OMSetRenderTargets(finalRtv, null);
        cmd.RSSetViewport(new Viewport(0, 0, OutW, OutH, 0f, 1f));
        cmd.RSSetScissorRect(OutW, OutH);
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cmd.DrawInstanced(3, 1, 0, 0);

        bool capture = offscreen && captureNext;
        if (offscreen)
        {
            if (capture)
            {
                Tr(offFinal!, ref offState, ResourceStates.CopySource);
                var fp = new PlacedSubresourceFootPrint { Offset = 0, Footprint = new SubresourceFootPrint(FinalFmt, (uint)OutW, (uint)OutH, 1, (uint)((OutW * 4 + 255) & ~255)) };
                captureBuf ??= CommittedBuffer(HeapType.Readback, (ulong)(((OutW * 4 + 255) & ~255) * OutH), ResourceFlags.None, ResourceStates.CopyDest);
                cmd.CopyTextureRegion(new TextureCopyLocation(captureBuf, fp), 0, 0, 0, new TextureCopyLocation(offFinal!, 0), null);
            }
            Tr(offFinal!, ref offState, ResourceStates.PixelShaderResource);
        }
        else cmd.ResourceBarrierTransition(finalRes, ResourceStates.RenderTarget, ResourceStates.Present, AllSub, ResourceBarrierFlags.None);

        cmd.EndQuery(queryHeap, QueryType.Timestamp, (uint)(fi * Ts + 4));
        cmd.ResolveQueryData(queryHeap, QueryType.Timestamp, (uint)(fi * Ts), 5, queryReadback, (ulong)(fi * Ts * 8));
        cmd.Close();
        queue.ExecuteCommandLists(new ID3D12CommandList[] { cmd });

        double tCpu = clock.Elapsed.TotalMilliseconds - t0;
        if (!offscreen)
        {
            PacingWait();
            var pf = (syncInterval == 0 && tearing) ? PresentFlags.AllowTearing : PresentFlags.None;
            var pr = swap!.Present((uint)syncInterval, pf);
            if (pr.Failure) HandleDeviceLoss(pr);
        }
        fenceValue++;
        queue.Signal(fence, fenceValue);
        frameFence[fi] = fenceValue;

        if (capture) { WaitForFence(fenceValue); captureReady = true; captureNext = false; }

        cur ^= 1;
        frameIndex++;
        TrackStats(dt, tCpu);
        Govern();
    }

    bool captureReady;
    public bool TryGetCapture(out byte[] bgra, out int w, out int h)
    {
        bgra = Array.Empty<byte>(); w = OutW; h = OutH;
        if (!captureReady || captureBuf == null) return false;
        captureReady = false;
        int pitch = (OutW * 4 + 255) & ~255;
        byte* p = captureBuf.Map<byte>(0);
        bgra = new byte[OutW * OutH * 4];
        for (int y = 0; y < OutH; y++)
        {
            uint* row = (uint*)(p + (long)y * pitch);
            for (int x = 0; x < OutW; x++)
            {
                uint v = row[x];
                int r = (int)(v & 1023) >> 2, g = (int)((v >> 10) & 1023) >> 2, b = (int)((v >> 20) & 1023) >> 2;
                int o = (y * OutW + x) * 4;
                bgra[o] = (byte)b; bgra[o + 1] = (byte)g; bgra[o + 2] = (byte)r; bgra[o + 3] = 255;
            }
        }
        captureBuf.Unmap(0);
        return true;
    }

    void HandleDeviceLoss(Result r)
    {
        var reason = device.DeviceRemovedReason;
        throw new InvalidOperationException($"Present failed: {r} (device removed reason: {reason})");
    }

    FrameCB BuildConstants(float dt)
    {
        var cb = new FrameCB();
        int active = (int)(capacity * Math.Clamp(set.Density / 150f, 0.15f, 1f) * particleFrac);
        active = Math.Clamp(active, 20_000, capacity);
        float fade = dir.GlobalFade;
        cb.ResX = rw; cb.ResY = rh; cb.InvResX = 1f / rw; cb.InvResY = 1f / rh;
        cb.Aspect = (float)OutW / OutH; cb.Time = dir.Time; cb.Dt = dt; cb.Frame = frameIndex;
        cb.Exposure = 1f; cb.Bloom = BloomBase; cb.Decay = 1.7f;
        cb.Inject = InjectBase * fade * (1_600_000f / active) * (rw * (float)rh) / (5120f * 1440f);
        cb.ParticleCount = (uint)active; cb.OriginCount = (uint)dir.Count;
        cb.GamutMode = hdrOut ? 1u : 0u; cb.OutMode = hdrOut ? 1u : 0u;
        cb.PaperWhite = 200f; cb.Hud = set.ShowDiagnostics ? 1f : 0f;
        cb.FlowSpeed = set.Speed / 100f * 1.6f; cb.Dither = 1f;
        cb.Haze = HazeBase * fade; cb.Detail = 1f; cb.ParticleSize = 1.1f * MathF.Max(scale, 0.6f);
        cb.Brightness = set.Brightness / 80f;
        cb.Advect = 0.9f;
        cb.SeedX = BitConverter.Int32BitsToSingle((int)(frameIndex * 2654435761u)); cb.SeedY = 0; cb.SeedZ = 0; cb.SeedW = 0;
        cb.FlowTexW = flow.W; cb.FlowTexH = flow.H; cb.HudW = HudW; cb.HudH = HudH;
        for (int i = 0; i < Director.Max * Director.FloatsPerOrigin; i++) cb.Origins[i] = dir.Gpu[i];
        activeNow = active;
        return cb;
    }
    int activeNow;
    public static float InjectBase = 0.07f, HazeBase = 0.012f, BloomBase = 0.35f;

    // ------------------------------------------------------------------ pacing, stats, adaptive quality
    void PacingWait()
    {
        // With vsync the present interval paces us. Without vsync (or with a cap below refresh and tearing) spin to the cap.
        if (ManualPacing || (syncInterval > 0 && set.VSync)) return;
        if (Stats.Cap <= 0) return;
        double interval = 1000.0 / Stats.Cap;
        double now = clock.Elapsed.TotalMilliseconds;
        if (nextFrameDeadline == 0 || now - nextFrameDeadline > interval * 3) nextFrameDeadline = now;
        nextFrameDeadline += interval;
        double remaining = nextFrameDeadline - now;
        if (remaining > 2.0) Thread.Sleep((int)(remaining - 1.5));
        while (clock.Elapsed.TotalMilliseconds < nextFrameDeadline) Thread.SpinWait(50);
    }

    void ReadTimestamps(int fi)
    {
        if (frameIndex < FrameCount) return;
        ulong* q = queryPtr + fi * Ts;
        double toMs = 1000.0 / tsFreq;
        if (q[4] > q[0])
        {
            gpuTotal = (q[4] - q[0]) * toMs;
            gpuSim = (q[1] - q[0]) * toMs;
            gpuFieldOnly = (q[2] - q[1]) * toMs;
            gpuParts = (q[3] - q[2]) * toMs;
            gpuField = gpuFieldOnly + gpuParts;
            gpuPost = (q[4] - q[3]) * toMs;
        }
    }

    void TrackStats(float dt, double cpuMs)
    {
        double now = clock.Elapsed.TotalSeconds;
        fpsAcc += now - lastFrameT; fpsCount++; lastFrameT = now;
        cpuMsAcc += cpuMs;
        if (fpsAcc >= 0.5)
        {
            fpsShown = fpsCount / fpsAcc;
            Stats.Fps = fpsShown; Stats.CpuMs = cpuMsAcc / fpsCount;
            fpsAcc = 0; fpsCount = 0; cpuMsAcc = 0;
        }
        Stats.GpuFeedbackMs = gpuFieldOnly; Stats.GpuParticlesMs = gpuParts; Stats.GpuMs = gpuTotal; Stats.GpuSimMs = gpuSim; Stats.GpuFieldMs = gpuField; Stats.GpuPostMs = gpuPost;
        Stats.RenderW = rw; Stats.RenderH = rh; Stats.OutW = OutW; Stats.OutH = OutH; Stats.Scale = scale;
        Stats.Particles = activeNow; Stats.ParticleFraction = particleFrac; Stats.Forms = dir.Count; Stats.Gpu = gpuName;
        Stats.Mode = hdrOut ? "HDR10 / Rec.2020, 10-bit" : "SDR sRGB, 10-bit dithered";
        if (adapter3 != null && (frameIndex & 31) == 0)
        {
            try { var m = adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local); Stats.VramUsed = (long)m.CurrentUsage; Stats.VramBudget = (long)m.Budget; } catch { }
        }
    }

    void Govern()
    {
        if (!set.AdaptiveQuality || offscreen) return;
        govFrames++; govGpuSum += gpuTotal;
        double now = clock.Elapsed.TotalSeconds;
        if (govFrames < 45 || now - lastChangeT < 2.5) return;
        double avg = govGpuSum / govFrames; govFrames = 0; govGpuSum = 0;
        double budget = 1000.0 / Math.Max(Stats.Cap, 24) * 0.88;
        if (avg <= 0) return;
        if (avg > budget)
        {
            if (particleFrac > 0.42f) particleFrac = MathF.Max(0.4f, particleFrac - 0.12f);
            else if (scale > minScale + 0.01f) { SetScale(MathF.Max(minScale, scale - 0.1f)); }
            else particleFrac = MathF.Max(0.25f, particleFrac - 0.1f);
            lastChangeT = now;
        }
        else if (avg < budget * 0.6)
        {
            if (scale < maxScale - 0.01f) { SetScale(MathF.Min(maxScale, scale + 0.1f)); lastChangeT = now; }
            else if (particleFrac < 1f) { particleFrac = MathF.Min(1f, particleFrac + 0.08f); lastChangeT = now; }
        }
    }

    void SetScale(float s)
    {
        s = MathF.Round(s * 20f) / 20f;
        if (MathF.Abs(s - scale) < 0.01f) return;
        WaitIdle();
        scale = s;
        CreateSizeDependent();
    }

    // ------------------------------------------------------------------ diagnostics HUD
    void UpdateHud(float dt)
    {
        if (!set.ShowDiagnostics) return;
        hudTimer += dt;
        if (hudTimer < 0.5 && frameIndex > 2) return;
        hudTimer = 0;
        using var bmp = new Bitmap(HudW, HudH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(150, 0, 0, 0));
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var f = new Font("Consolas", 10.5f);
            using var br = new SolidBrush(Color.FromArgb(255, 200, 255, 200));
            var s = Stats;
            string txt =
                $"Fluxbloom  [{name}]  {s.Gpu}\n" +
                $"{s.Fps,6:F1} fps  cap {s.Cap}  ({s.Hz:F0} Hz)  cpu {s.CpuMs:F2} ms\n" +
                $"gpu {s.GpuMs:F2} ms  sim {s.GpuSimMs:F2}  feedback {s.GpuFeedbackMs:F2}  parts {s.GpuParticlesMs:F2}  post {s.GpuPostMs:F2}\n" +
                $"render {s.RenderW}x{s.RenderH}  out {s.OutW}x{s.OutH}  scale {s.Scale:F2}\n" +
                $"particles {s.Particles:N0}  ({s.ParticleFraction:P0})  forms {s.Forms}\n" +
                $"vram {s.VramUsed / 1048576.0:F0} / {s.VramBudget / 1048576.0:F0} MB\n" +
                $"{s.Mode}";
            g.DrawString(txt, f, br, 6, 4);
        }
        var bd = bmp.LockBits(new Rectangle(0, 0, HudW, HudH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        Buffer.MemoryCopy((void*)bd.Scan0, hudPtr, HudW * HudH * 4, HudW * HudH * 4);
        bmp.UnlockBits(bd);
        hudDirty = true;
    }

    public void Dispose()
    {
        try { WaitIdle(); } catch { }
        foreach (var r in cbUpload) r?.Dispose();
        particles?.Dispose(); hudTex?.Dispose(); hudUpload?.Dispose(); offFinal?.Dispose(); captureBuf?.Dispose();
        queryReadback?.Dispose(); queryHeap?.Dispose();
        foreach (var t in scene) t?.Res.Dispose();
        foreach (var t in bloom) t?.Res.Dispose();
        flow?.Res.Dispose();
        foreach (var b in backBuffers) b?.Dispose();
        swap?.Dispose(); cmd?.Dispose(); foreach (var a in allocs) a?.Dispose(); fence?.Dispose(); queue?.Dispose();
        rootSig?.Dispose(); psoField?.Dispose(); psoParticles?.Dispose(); psoDown?.Dispose(); psoUp?.Dispose(); psoComposite?.Dispose(); psoUpdate?.Dispose(); psoFlow?.Dispose();
        rtvHeap?.Dispose(); srvHeap?.Dispose(); device?.Dispose(); adapter3?.Dispose(); adapter?.Dispose(); factory?.Dispose();
    }
}

static class NativeMethods
{
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
    [DllImport("user32.dll")] public static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);
    [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency, dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    public static float RefreshOf(string deviceName)
    {
        var dm = new DEVMODE { dmDeviceName = new string('\0', 32), dmFormName = new string('\0', 32), dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (EnumDisplaySettings(deviceName, -1, ref dm) && dm.dmDisplayFrequency > 1) return dm.dmDisplayFrequency;
        return 60f;
    }
}
