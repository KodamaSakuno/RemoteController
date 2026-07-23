using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteController.Host;

/// <summary>
/// Captures the primary display via DXGI Desktop Duplication. Acquired frames arrive in
/// the panel's native scanout orientation; when the display runs in a rotated mode (e.g.
/// a portrait panel set to landscape) they are rotated back to the desktop orientation on
/// the GPU. Two output paths:
/// <see cref="CaptureFrame"/> renders the frame into an NV12 texture entirely on the GPU
/// (for hardware encoder MFTs fed via DXGI surfaces); <see cref="CaptureNv12"/> reads back
/// and converts on the CPU (for the software encoder MFT, which takes system memory).
/// Both return null when the desktop has not changed, so an idle desktop costs neither
/// bandwidth nor encode CPU. Windows 8+ only.
/// </summary>
public sealed class DxgiScreenCapture : IDisposable
{
    private const uint AcquireTimeoutMs = 100;
    private const int Nv12PoolSize = 4;

    // Textured fullscreen quad: identity UV mapping (also used by the rotation pass with
    // permuted UVs baked into the vertex buffer).
    private const string QuadShaderSource = """
        struct VsInput
        {
            float3 pos : POSITION;
            float2 uv : TEXCOORD;
        };

        struct VsOutput
        {
            float4 pos : SV_POSITION;
            float2 uv : TEXCOORD;
        };

        VsOutput VSMain(VsInput input)
        {
            VsOutput output;
            output.pos = float4(input.pos, 1.0f);
            output.uv = input.uv;
            return output;
        }

        Texture2D frame : register(t0);
        SamplerState frameSampler : register(s0);

        float4 PSMain(VsOutput input) : SV_TARGET
        {
            return frame.Sample(frameSampler, input.uv);
        }

        // BT.601 studio-swing luma, matching the CPU conversion.
        float4 PSLuma(VsOutput input) : SV_TARGET
        {
            float3 c = frame.Sample(frameSampler, input.uv).rgb;
            float y = 0.0627 + 0.2578 * c.r + 0.5039 * c.g + 0.0977 * c.b;
            return float4(y, 0.0, 0.0, 1.0);
        }

        // BT.601 studio-swing chroma at half resolution (bilinear tap ~= 2x2 average).
        float4 PSChroma(VsOutput input) : SV_TARGET
        {
            float3 c = frame.Sample(frameSampler, input.uv).rgb;
            float u = 0.502 - 0.1484 * c.r - 0.2891 * c.g + 0.4375 * c.b;
            float v = 0.502 + 0.4375 * c.r - 0.3672 * c.g - 0.0703 * c.b;
            return float4(u, v, 0.0, 1.0);
        }
        """;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutput1 _output;

    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;
    private byte[]? _nv12;

    // NV12 textures handed to the encoder; pooled because the MFT may still reference a
    // surface while we render the next frame into another one. The Y/UV planes are
    // rendered into plain R8/R8G8 textures (universally renderable) and then copied into
    // the NV12 texture's planes — Adreno rejects NV12 as a draw-time render target.
    private readonly ID3D11Texture2D?[] _nv12Pool = new ID3D11Texture2D?[Nv12PoolSize];
    private readonly ID3D11Texture2D?[] _yPlanePool = new ID3D11Texture2D?[Nv12PoolSize];
    private readonly ID3D11Texture2D?[] _uvPlanePool = new ID3D11Texture2D?[Nv12PoolSize];
    private readonly ID3D11RenderTargetView?[] _yRtvs = new ID3D11RenderTargetView?[Nv12PoolSize];
    private readonly ID3D11RenderTargetView?[] _uvRtvs = new ID3D11RenderTargetView?[Nv12PoolSize];
    private int _nv12Index;

    // Pooled BGRA frame textures for encoders that accept RGB32 input directly.
    private readonly ID3D11Texture2D?[] _bgraPool = new ID3D11Texture2D?[Nv12PoolSize];
    private readonly ID3D11RenderTargetView?[] _bgraRtvs = new ID3D11RenderTargetView?[Nv12PoolSize];
    private int _bgraIndex;
    private int _bgraPoolWidth;
    private int _bgraPoolHeight;

    // GPU rotation pipeline, created lazily and only used when the display is rotated.
    private ID3D11Texture2D? _rotated;
    private ID3D11RenderTargetView? _rotatedRtv;

    // Shared fullscreen-quad pipeline (rotation and NV12 conversion alike).
    private ID3D11VertexShader? _quadVertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11PixelShader? _lumaShader;
    private ID3D11PixelShader? _chromaShader;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11SamplerState? _sampler;
    private ID3D11RasterizerState? _rasterizerState;
    private ID3D11Buffer? _quadVertices;
    private ID3D11Buffer? _identityQuadVertices;
    private ModeRotation _quadRotation = ModeRotation.Unspecified;

    private ModeRotation _rotation;
    private int _rawWidth;
    private int _rawHeight;
    private int _catchUpDiscards;

    /// <summary>Stale frames discarded by the acquisition catch-up (diagnostics).</summary>
    public int CatchUpDiscards => _catchUpDiscards;

    /// <summary>The D3D device all frames and NV12 textures live on.</summary>
    public ID3D11Device Device => _device;

    public DxgiScreenCapture()
    {
        (_device, _context, _output) = CreateDuplicationSource();
        Width = _output.Description.DesktopCoordinates.Right;
        Height = _output.Description.DesktopCoordinates.Bottom;
        RecreateDuplication();
    }

    /// <summary>Primary display size in physical pixels, in the desktop's orientation.</summary>
    public int Width { get; private set; }

    /// <summary>Primary display size in physical pixels, in the desktop's orientation.</summary>
    public int Height { get; private set; }

    /// <summary>
    /// Opts the process into physical-pixel reporting. Without this, Windows virtualizes
    /// DXGI output coordinates for DPI-unaware processes (e.g. reporting 1536x864 on a
    /// 1920x1080/125% display), which would corrupt the size sent in the handshake.
    /// Must be called before the first DXGI call.
    /// </summary>
    public static void EnsureDpiAwareness() => SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    /// <summary>
    /// Captures the next desktop update and converts it to NV12 on the CPU (system memory),
    /// or returns null when no update arrived within the acquire timeout. The returned
    /// buffer is reused on the next call.
    /// </summary>
    public byte[]? CaptureNv12() => WithFrame(texture =>
    {
        var source = texture;
        if (_rotation is not (ModeRotation.Identity or ModeRotation.Unspecified))
        {
            // The staging texture is desktop-oriented, so rotate on the GPU first.
            RotateFrame(texture, _rotated!, _rotatedRtv!);
            source = _rotated!;
        }

        _context.CopyResource(_staging!, source);
        return ReadbackNv12();
    });

    /// <summary>
    /// Captures the next desktop update as a plain BGRA texture (rotation applied), pooled
    /// for the encoder to hold. Returns null when no update arrived within the timeout.
    /// </summary>
    public ID3D11Texture2D? CaptureBgra() => WithFrame(texture =>
    {
        EnsureBgraPool();
        var slot = _bgraIndex;
        _bgraIndex = (_bgraIndex + 1) % Nv12PoolSize;

        if (_rotation is not (ModeRotation.Identity or ModeRotation.Unspecified))
            RotateFrame(texture, _bgraPool[slot]!, _bgraRtvs[slot]!);
        else
            _context.CopyResource(_bgraPool[slot]!, texture);

        return _bgraPool[slot]!;
    });

    /// <summary>
    /// Captures the next desktop update and renders it into a pooled NV12 texture entirely
    /// on the GPU (rotation and BT.601 conversion included), or returns null when no update
    /// arrived within the acquire timeout.
    /// </summary>
    public ID3D11Texture2D? CaptureFrame() => WithFrame(texture =>
    {
        var source = texture;
        if (_rotation is not (ModeRotation.Identity or ModeRotation.Unspecified))
        {
            RotateFrame(texture, _rotated!, _rotatedRtv!);
            source = _rotated!;
        }

        return ConvertToNv12(source);
    });

    /// <summary>Runs the shared acquire/rotate prologue and hands the newest frame to <paramref name="produce"/>.</summary>
    private T? WithFrame<T>(Func<ID3D11Texture2D, T?> produce) where T : class
    {
        if (_duplication is null)
            RecreateDuplication();

        var result = _duplication!.AcquireNextFrame(AcquireTimeoutMs, out var frameInfo, out var resource);
        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
            return null; // desktop unchanged
        if (result == Vortice.DXGI.ResultCode.AccessDenied)
        {
            // Secure desktop (UAC prompt, lock screen) cannot be captured; back off briefly.
            Thread.Sleep(200);
            return null;
        }

        if (result == Vortice.DXGI.ResultCode.AccessLost)
        {
            // Display mode change or session switch: the duplication must be recreated.
            RecreateDuplication();
            return null;
        }

        result.CheckError();

        // Catch up only when genuinely behind. Desktop Duplication queues updates while we
        // are busy encoding; processing the oldest one shows the past. A couple of queued
        // frames is just normal pacing (the desktop ticks faster than our loop), so drain
        // only once the backlog is real — otherwise we would discard the newest frame too.
        const int CatchUpThreshold = 2;
        while (frameInfo.AccumulatedFrames > CatchUpThreshold)
        {
            // A frame may not be outstanding when acquiring the next, so release first.
            resource?.Dispose();
            _duplication.ReleaseFrame();

            var next = _duplication.AcquireNextFrame(0, out frameInfo, out var newer);
            if (next == Vortice.DXGI.ResultCode.WaitTimeout)
                return null; // drained in the meantime; the next capture gets a fresh frame
            if (next == Vortice.DXGI.ResultCode.AccessDenied || next == Vortice.DXGI.ResultCode.AccessLost)
            {
                newer?.Dispose();
                if (next == Vortice.DXGI.ResultCode.AccessDenied)
                    Thread.Sleep(200);
                else
                    RecreateDuplication();
                return null;
            }

            next.CheckError();

            resource = newer;
            if (++_catchUpDiscards % 300 == 1)
                Console.WriteLine($"[Host] catching up on capture queue; {_catchUpDiscards} stale frames discarded");
        }

        try
        {
            using var texture = resource!.QueryInterface<ID3D11Texture2D>();
            EnsureBuffers((int)texture.Description.Width, (int)texture.Description.Height);
            return produce(texture);
        }
        finally
        {
            resource?.Dispose();
            _duplication.ReleaseFrame();
        }
    }

    public void Dispose()
    {
        _duplication?.Dispose();
        _staging?.Dispose();
        for (var i = 0; i < Nv12PoolSize; i++)
        {
            _yRtvs[i]?.Dispose();
            _uvRtvs[i]?.Dispose();
            _yPlanePool[i]?.Dispose();
            _uvPlanePool[i]?.Dispose();
            _nv12Pool[i]?.Dispose();
            _bgraRtvs[i]?.Dispose();
            _bgraPool[i]?.Dispose();
        }

        _rotatedRtv?.Dispose();
        _rotated?.Dispose();
        _quadVertices?.Dispose();
        _identityQuadVertices?.Dispose();
        _sampler?.Dispose();
        _rasterizerState?.Dispose();
        _inputLayout?.Dispose();
        _lumaShader?.Dispose();
        _chromaShader?.Dispose();
        _pixelShader?.Dispose();
        _quadVertexShader?.Dispose();
        _output.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    private static (ID3D11Device Device, ID3D11DeviceContext Context, IDXGIOutput1 Output) CreateDuplicationSource()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint adapterIndex = 0; factory.EnumAdapters(adapterIndex, out var adapter).Success; adapterIndex++)
        {
            using (adapter)
            {
                for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out var output).Success; outputIndex++)
                {
                    using (output)
                    {
                        // The primary display always starts at the virtual-screen origin.
                        var bounds = output.Description.DesktopCoordinates;
                        if (bounds.Left != 0 || bounds.Top != 0)
                            continue;

                        D3D11.D3D11CreateDevice(
                                adapter,
                                DriverType.Unknown,
                                DeviceCreationFlags.None,
                                new[] { FeatureLevel.Level_11_0 },
                                out ID3D11Device? device,
                                out ID3D11DeviceContext? context)
                            .CheckError();

                        return (device!, context!, output.QueryInterface<IDXGIOutput1>());
                    }
                }
            }
        }

        throw new InvalidOperationException("No DXGI output found for the primary display.");
    }

    private void RecreateDuplication()
    {
        _duplication?.Dispose();
        _duplication = _output.DuplicateOutput(_device);

        // Acquired frames are in the panel's native scanout orientation. When the display
        // runs rotated, they must be rotated by the named amount to match the desktop.
        _rotation = _duplication.Description.Rotation;
        if (_rotation is not (ModeRotation.Identity or ModeRotation.Unspecified))
            Console.WriteLine($"[Host] Display rotation is {_rotation}; frames will be rotated to the desktop orientation.");
    }

    private void EnsureBuffers(int rawWidth, int rawHeight)
    {
        var needsRotation = _rotation is not (ModeRotation.Identity or ModeRotation.Unspecified);
        if (_staging is not null && _rawWidth == rawWidth && _rawHeight == rawHeight
            && (_rotated is not null) == needsRotation)
            return;

        _staging?.Dispose();
        _rotatedRtv?.Dispose();
        _rotated?.Dispose();
        _rotatedRtv = null;
        _rotated = null;
        for (var i = 0; i < Nv12PoolSize; i++)
        {
            _yRtvs[i]?.Dispose();
            _uvRtvs[i]?.Dispose();
            _yPlanePool[i]?.Dispose();
            _uvPlanePool[i]?.Dispose();
            _nv12Pool[i]?.Dispose();
        }

        _rawWidth = rawWidth;
        _rawHeight = rawHeight;

        // The public dimensions are in the desktop's orientation, which is what the
        // handshake reports and what input coordinates are mapped against. All buffers
        // hold the corrected (desktop-oriented) image.
        var swapped = _rotation is ModeRotation.Rotate90 or ModeRotation.Rotate270;
        Width = swapped ? rawHeight : rawWidth;
        Height = swapped ? rawWidth : rawHeight;

        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        _nv12 = new byte[Width * Height * 3 / 2];

        for (var i = 0; i < Nv12PoolSize; i++)
        {
            try
            {
                // NV12 texture: copy destination and encoder input only, never a render
                // target (Adreno rejects NV12 at draw time). SHARED matches what OBS
                // hands to encoder MFTs — hardware encoders may open the surface from
                // their own internal device and require it to be shareable.
                _nv12Pool[i] = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)Width,
                    Height = (uint)Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.NV12,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.SharedKeyedMutex,
                });
                _yPlanePool[i] = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)Width,
                    Height = (uint)Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.R8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget,
                    CPUAccessFlags = CpuAccessFlags.None,
                });
                _uvPlanePool[i] = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)(Width / 2),
                    Height = (uint)(Height / 2),
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.R8G8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget,
                    CPUAccessFlags = CpuAccessFlags.None,
                });
                _yRtvs[i] = _device.CreateRenderTargetView(_yPlanePool[i]);
                _uvRtvs[i] = _device.CreateRenderTargetView(_uvPlanePool[i]);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"NV12 pool/RTV creation failed: {ex.Message}", ex);
            }
        }

        if (needsRotation)
        {
            _rotated = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)Width,
                Height = (uint)Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
            });
            _rotatedRtv = _device.CreateRenderTargetView(_rotated);
        }
    }

    /// <summary>Draws the scanout-oriented frame into the given desktop-oriented render target.</summary>
    private void RotateFrame(ID3D11Texture2D source, ID3D11Texture2D target, ID3D11RenderTargetView targetRtv)
    {
        EnsureQuadResources();

        if (_quadVertices is null || _quadRotation != _rotation)
            RebuildQuadVertices();

        using var view = _device.CreateShaderResourceView(source);
        BeginQuad(view);
        _context.PSSetShader(_pixelShader!);
        _context.OMSetRenderTargets(targetRtv);
        _context.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, Width, Height));
        _context.Draw(6, 0);
    }

    /// <summary>Renders the frame into the next pooled NV12 texture (identity UVs).</summary>
    private ID3D11Texture2D ConvertToNv12(ID3D11Texture2D source)
    {
        EnsureQuadResources();
        if (_identityQuadVertices is null)
            _identityQuadVertices = CreateQuadVertices(
                (0f, 0f), (1f, 0f), (0f, 1f), (1f, 1f)); // TL, TR, BL, BR

        var stage = "source view";
        try
        {
            var index = _nv12Index;
            _nv12Index = (_nv12Index + 1) % Nv12PoolSize;

            using var view = _device.CreateShaderResourceView(source);
            stage = "quad state";
            BeginQuad(view, _identityQuadVertices!);

            // Render into plain R8/R8G8 plane textures (universally renderable), then
            // copy the planes into the NV12 texture — Adreno rejects NV12 at draw time.
            stage = "luma draw";
            _context.PSSetShader(_lumaShader!);
            _context.OMSetRenderTargets(_yRtvs[index]!);
            _context.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, Width, Height));
            _context.Draw(6, 0);

            stage = "chroma draw";
            _context.PSSetShader(_chromaShader!);
            _context.OMSetRenderTargets(_uvRtvs[index]!);
            _context.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, Width / 2, Height / 2));
            _context.Draw(6, 0);

            stage = "plane copy";
            _context.CopySubresourceRegion(_nv12Pool[index]!, 0, 0, 0, 0, _yPlanePool[index]!, 0, null);
            _context.CopySubresourceRegion(_nv12Pool[index]!, 1, 0, 0, 0, _uvPlanePool[index]!, 0, null);

            return _nv12Pool[index]!;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"NV12 conversion ({stage}) failed: {ex.Message}", ex);
        }
    }

    /// <summary>Creates (or recreates) the pooled BGRA frame textures when dimensions change.</summary>
    private void EnsureBgraPool()
    {
        if (_bgraPool[0] is not null && _bgraPoolWidth == Width && _bgraPoolHeight == Height)
            return;

        for (var i = 0; i < Nv12PoolSize; i++)
        {
            _bgraRtvs[i]?.Dispose();
            _bgraPool[i]?.Dispose();
            _bgraPool[i] = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)Width,
                Height = (uint)Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
            });
            _bgraRtvs[i] = _device.CreateRenderTargetView(_bgraPool[i]);
        }

        _bgraPoolWidth = Width;
        _bgraPoolHeight = Height;
    }

    /// <summary>Sets up the shared quad pipeline state for a draw from <paramref name="source"/>.</summary>
    private void BeginQuad(ID3D11ShaderResourceView source, ID3D11Buffer? vertices = null)
    {
        _context.IASetInputLayout(_inputLayout);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetVertexBuffer(0, vertices ?? _quadVertices!, 5 * sizeof(float), 0);
        _context.VSSetShader(_quadVertexShader!);
        _context.RSSetState(_rasterizerState);
        _context.PSSetSampler(0, _sampler!);
        _context.PSSetShaderResource(0, source);
    }

    private void EnsureQuadResources()
    {
        if (_quadVertexShader is not null)
            return;

        var shaderBytes = System.Text.Encoding.ASCII.GetBytes(QuadShaderSource);
        var vsResult = Compiler.Compile(shaderBytes, "VSMain", "QuadShader", "vs_4_0",
            out var vsBlob, out var vsErrors);
        if (vsResult.Failure)
            throw new InvalidOperationException($"Vertex shader compile failed: {vsErrors?.AsString()}");
        var psResult = Compiler.Compile(shaderBytes, "PSMain", "QuadShader", "ps_4_0",
            out var psBlob, out var psErrors);
        if (psResult.Failure)
            throw new InvalidOperationException($"Pixel shader compile failed: {psErrors?.AsString()}");
        var lumaResult = Compiler.Compile(shaderBytes, "PSLuma", "QuadShader", "ps_4_0",
            out var lumaBlob, out var lumaErrors);
        if (lumaResult.Failure)
            throw new InvalidOperationException($"Luma shader compile failed: {lumaErrors?.AsString()}");
        var chromaResult = Compiler.Compile(shaderBytes, "PSChroma", "QuadShader", "ps_4_0",
            out var chromaBlob, out var chromaErrors);
        if (chromaResult.Failure)
            throw new InvalidOperationException($"Chroma shader compile failed: {chromaErrors?.AsString()}");

        _quadVertexShader = _device.CreateVertexShader(vsBlob!.AsSpan());
        _pixelShader = _device.CreatePixelShader(psBlob!.AsSpan());
        _lumaShader = _device.CreatePixelShader(lumaBlob!.AsSpan());
        _chromaShader = _device.CreatePixelShader(chromaBlob!.AsSpan());
        _inputLayout = _device.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0, InputClassification.PerVertexData, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 12, 0, InputClassification.PerVertexData, 0),
        }, vsBlob.AsSpan());
        _sampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MipLODBias = 0,
            MaxAnisotropy = 1,
            ComparisonFunc = ComparisonFunction.Never,
            BorderColor = new Vortice.Mathematics.Color4(0, 0, 0, 0),
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        });
        _rasterizerState = _device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
    }

    /// <summary>
    /// Rebuilds the fullscreen quad with UVs permuted so that drawing the scanout-oriented
    /// source produces the desktop-oriented image. Corner mapping follows the same
    /// convention as DXGI's dirty-rect rotation: the image is rotated clockwise by the
    /// amount named in <see cref="ModeRotation"/>.
    /// </summary>
    private void RebuildQuadVertices()
    {
        // UVs per destination corner: top-left, top-right, bottom-left, bottom-right.
        var (tl, tr, bl, br) = _rotation switch
        {
            // Rotate90 (90° CW): dest TL samples the source's bottom-left corner.
            ModeRotation.Rotate90 => ((0f, 1f), (0f, 0f), (1f, 1f), (1f, 0f)),
            // Rotate180: dest TL samples the source's bottom-right corner.
            ModeRotation.Rotate180 => ((1f, 1f), (0f, 1f), (1f, 0f), (0f, 0f)),
            // Rotate270 (270° CW): dest TL samples the source's top-right corner.
            _ => ((1f, 0f), (1f, 1f), (0f, 0f), (0f, 1f)),
        };

        _quadVertices?.Dispose();
        _quadVertices = CreateQuadVertices(tl, tr, bl, br);
        _quadRotation = _rotation;
    }

    private ID3D11Buffer CreateQuadVertices((float U, float V) tl, (float U, float V) tr, (float U, float V) bl, (float U, float V) br)
    {
        // Two triangles covering the full quad: (BL, TL, TR) and (TR, BL, BR).
        float[] vertices =
        {
            // x, y, z, u, v
            -1, -1, 0, bl.U, bl.V,
            -1,  1, 0, tl.U, tl.V,
             1,  1, 0, tr.U, tr.V,
             1,  1, 0, tr.U, tr.V,
            -1, -1, 0, bl.U, bl.V,
             1, -1, 0, br.U, br.V,
        };

        unsafe
        {
            fixed (float* p = vertices)
            {
                return _device.CreateBuffer(
                    new BufferDescription
                    {
                        ByteWidth = (uint)(vertices.Length * sizeof(float)),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.VertexBuffer,
                        CPUAccessFlags = CpuAccessFlags.None,
                    },
                    new SubresourceData((IntPtr)p));
            }
        }
    }

    /// <summary>Copies the staging texture out and converts BGRA to NV12 (BT.601 studio swing).</summary>
    private byte[] ReadbackNv12()
    {
        var map = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var nv12 = _nv12!;
            unsafe
            {
                var src = (byte*)map.DataPointer;
                fixed (byte* dst = nv12)
                    BgraToNv12(src, (int)map.RowPitch, dst, Width, Height);
            }

            return nv12;
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }
    }

    private static unsafe void BgraToNv12(byte* src, int srcPitch, byte* dst, int width, int height)
    {
        var uvPlane = dst + width * height;
        // Rows are independent; a scalar per-pixel loop is the host's main CPU cost at
        // high resolutions, so convert in parallel.
        Parallel.For(0, height, y =>
        {
            var srcRow = src + y * srcPitch;
            var dstRow = dst + y * width;
            for (var x = 0; x < width; x++)
            {
                var b = srcRow[x * 4];
                var g = srcRow[x * 4 + 1];
                var r = srcRow[x * 4 + 2];
                dstRow[x] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
            }
        });

        // 2x2-subsampled interleaved chroma; desktop modes always have even dimensions.
        Parallel.For(0, height / 2, y2 =>
        {
            var y = y2 * 2;
            var srcRow0 = src + y * srcPitch;
            var srcRow1 = srcRow0 + srcPitch;
            var uvRow = uvPlane + y2 * width;
            for (var x = 0; x < width; x += 2)
            {
                var b = (srcRow0[x * 4] + srcRow0[x * 4 + 4] + srcRow1[x * 4] + srcRow1[x * 4 + 4] + 2) >> 2;
                var g = (srcRow0[x * 4 + 1] + srcRow0[x * 4 + 5] + srcRow1[x * 4 + 1] + srcRow1[x * 4 + 5] + 2) >> 2;
                var r = (srcRow0[x * 4 + 2] + srcRow0[x * 4 + 6] + srcRow1[x * 4 + 2] + srcRow1[x * 4 + 6] + 2) >> 2;
                uvRow[x] = (byte)((-38 * r - 74 * g + 112 * b + 128 >> 8) + 128);
                uvRow[x + 1] = (byte)((112 * r - 94 * g - 18 * b + 128 >> 8) + 128);
            }
        });
    }
}
