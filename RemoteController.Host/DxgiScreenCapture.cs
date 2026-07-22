using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteController.Host;

/// <summary>
/// Captures the primary display via DXGI Desktop Duplication and encodes frames as JPEG.
/// Acquired frames arrive in the panel's native scanout orientation; when the display runs
/// in a rotated mode (e.g. a portrait panel set to landscape) they are rotated back to the
/// desktop orientation on the GPU (a fullscreen quad with rotated UVs) before readback.
/// <see cref="CaptureJpeg"/> returns null when the desktop has not changed, so an idle
/// desktop costs neither bandwidth nor encode CPU. Windows 8+ only.
/// </summary>
public sealed class DxgiScreenCapture : IDisposable
{
    private const uint AcquireTimeoutMs = 100;

    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    // Textured fullscreen quad used to rotate a frame on the GPU.
    private const string RotationShaderSource = """
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
        """;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutput1 _output;
    private readonly EncoderParameters _jpegParams;

    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;
    private Bitmap? _encodeBuffer;

    // GPU rotation pipeline, created lazily and only used when the display is rotated.
    private ID3D11Texture2D? _rotated;
    private ID3D11RenderTargetView? _rotatedRtv;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11SamplerState? _sampler;
    private ID3D11RasterizerState? _rasterizerState;
    private ID3D11Buffer? _quadVertices;
    private ModeRotation _quadRotation = ModeRotation.Unspecified;

    private ModeRotation _rotation;
    private int _rawWidth;
    private int _rawHeight;

    public DxgiScreenCapture(long quality = 70)
    {
        _jpegParams = new EncoderParameters(1);
        _jpegParams.Param[0] = new EncoderParameter(Encoder.Quality, quality);

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
    /// Captures and JPEG-encodes the next desktop update,
    /// or returns null when no update arrived within the acquire timeout.
    /// </summary>
    public byte[]? CaptureJpeg()
    {
        if (_duplication is null)
            RecreateDuplication();

        var result = _duplication!.AcquireNextFrame(AcquireTimeoutMs, out _, out var resource);
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

        try
        {
            using var texture = resource!.QueryInterface<ID3D11Texture2D>();
            EnsureBuffers((int)texture.Description.Width, (int)texture.Description.Height);
            if (_rotation is ModeRotation.Identity or ModeRotation.Unspecified)
            {
                _context.CopyResource(_staging!, texture);
            }
            else
            {
                // The staging texture is desktop-oriented, so rotate on the GPU first.
                RotateFrame(texture);
                _context.CopyResource(_staging!, _rotated!);
            }

            return EncodeFrame();
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
        _encodeBuffer?.Dispose();
        _rotatedRtv?.Dispose();
        _rotated?.Dispose();
        _quadVertices?.Dispose();
        _sampler?.Dispose();
        _rasterizerState?.Dispose();
        _inputLayout?.Dispose();
        _vertexShader?.Dispose();
        _pixelShader?.Dispose();
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
        _encodeBuffer?.Dispose();
        _rotatedRtv?.Dispose();
        _rotated?.Dispose();
        _rotatedRtv = null;
        _rotated = null;

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
        _encodeBuffer = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);

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
                BindFlags = BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
            });
            _rotatedRtv = _device.CreateRenderTargetView(_rotated);
        }
    }

    /// <summary>Draws the scanout-oriented frame into the desktop-oriented render target.</summary>
    private void RotateFrame(ID3D11Texture2D source)
    {
        EnsureRenderResources();

        using var view = _device.CreateShaderResourceView(source);
        _context.IASetInputLayout(_inputLayout);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetVertexBuffer(0, _quadVertices!, 5 * sizeof(float), 0);
        _context.VSSetShader(_vertexShader!);
        _context.RSSetState(_rasterizerState);
        _context.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, Width, Height));
        _context.PSSetShader(_pixelShader!);
        _context.PSSetSampler(0, _sampler!);
        _context.PSSetShaderResource(0, view);
        _context.OMSetRenderTargets(_rotatedRtv!);
        _context.Draw(6, 0);
    }

    private void EnsureRenderResources()
    {
        if (_vertexShader is null)
        {
            var shaderBytes = System.Text.Encoding.ASCII.GetBytes(RotationShaderSource);
            var vsResult = Compiler.Compile(shaderBytes, "VSMain", "RotationShader", "vs_4_0",
                out var vsBlob, out var vsErrors);
            if (vsResult.Failure)
                throw new InvalidOperationException($"Vertex shader compile failed: {vsErrors?.AsString()}");
            var psResult = Compiler.Compile(shaderBytes, "PSMain", "RotationShader", "ps_4_0",
                out var psBlob, out var psErrors);
            if (psResult.Failure)
                throw new InvalidOperationException($"Pixel shader compile failed: {psErrors?.AsString()}");

            _vertexShader = _device.CreateVertexShader(vsBlob!.AsSpan());
            _pixelShader = _device.CreatePixelShader(psBlob!.AsSpan());
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

        if (_quadVertices is null || _quadRotation != _rotation)
            RebuildQuadVertices();
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

        // Two triangles covering the full quad: (BL, TL, TR) and (TR, BL, BR).
        float[] vertices =
        {
            // x, y, z, u, v
            -1, -1, 0, bl.Item1, bl.Item2,
            -1,  1, 0, tl.Item1, tl.Item2,
             1,  1, 0, tr.Item1, tr.Item2,
             1,  1, 0, tr.Item1, tr.Item2,
            -1, -1, 0, bl.Item1, bl.Item2,
             1, -1, 0, br.Item1, br.Item2,
        };

        _quadVertices?.Dispose();
        unsafe
        {
            fixed (float* p = vertices)
            {
                _quadVertices = _device.CreateBuffer(
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

        _quadRotation = _rotation;
    }

    private byte[] EncodeFrame()
    {
        var map = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var data = _encodeBuffer!.LockBits(
                new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                // DXGI gives BGRA rows, which is exactly Format32bppArgb's layout.
                var rowBytes = Width * 4;
                unsafe
                {
                    var src = (byte*)map.DataPointer;
                    var dst = (byte*)data.Scan0;
                    for (var y = 0; y < Height; y++)
                        Buffer.MemoryCopy(src + y * map.RowPitch, dst + y * data.Stride, rowBytes, rowBytes);
                }
            }
            finally
            {
                _encodeBuffer.UnlockBits(data);
            }
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }

        using var ms = new MemoryStream();
        _encodeBuffer!.Save(ms, JpegCodec, _jpegParams);
        return ms.ToArray();
    }
}
