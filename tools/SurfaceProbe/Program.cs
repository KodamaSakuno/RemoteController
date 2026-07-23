// Standalone probe: does this machine's MF layer accept NV12 DXGI surface buffers,
// and in which texture form? Prints a verdict per variant. Safe to run anywhere.
// Usage: dotnet run --project tools/SurfaceProbe

using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

const int W = 1280, H = 720;
var mftFriendlyName = new Guid("314ffbae-5b41-4c95-9c19-4e7d586face3");
var texture2DIid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c"); // IID_ID3D11Texture2D

// --- D3D11 device on the primary adapter (same as the capture pipeline) ---
ID3D11Device? device = null;
ID3D11DeviceContext? context = null;
using (var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>())
{
    factory.EnumAdapters(0, out var adapter).CheckError();
    using (adapter)
    {
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.None,
            new[] { FeatureLevel.Level_11_0 }, out device, out context).CheckError();
    }
}
Console.WriteLine("device created.");

var variants = new (string Name, BindFlags Bind, ResourceOptionFlags Misc)[]
{
    ("RT", BindFlags.RenderTarget, ResourceOptionFlags.None),
    ("RT|SRV", BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceOptionFlags.None),
    ("RT|Shared", BindFlags.RenderTarget, ResourceOptionFlags.Shared),
    ("RT|SharedKeyedMutex", BindFlags.RenderTarget, ResourceOptionFlags.SharedKeyedMutex),
    ("RT|VideoEncoder", BindFlags.RenderTarget | BindFlags.VideoEncoder, ResourceOptionFlags.None),
};

var passedTextures = new List<(string Name, ID3D11Texture2D Tex)>();
foreach (var (name, bind, misc) in variants)
{
    ID3D11Texture2D tex;
    try
    {
        tex = device!.CreateTexture2D(new Texture2DDescription
        {
            Width = W, Height = H, MipLevels = 1, ArraySize = 1, Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = bind, CPUAccessFlags = CpuAccessFlags.None, MiscFlags = misc,
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{name,-20} texture creation FAILED: {ex.Message}");
        continue;
    }

    try
    {
        using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(texture2DIid, tex, 0, new RawBool(false));
        Console.WriteLine($"{name,-20} MFCreateDXGISurfaceBuffer: OK");
        passedTextures.Add((name, tex));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{name,-20} MFCreateDXGISurfaceBuffer: FAILED {HResult(ex)}");
        tex.Dispose();
    }
}

if (passedTextures.Count == 0)
{
    Console.WriteLine("VERDICT: no NV12 texture form is accepted as a DXGI surface buffer.");
    Console.WriteLine("=> hardware encoder cannot be fed textures here; system-memory input only.");
    return;
}

// --- try ProcessInput on the hardware encoder with each passing variant ---
MediaFactory.MFStartup(true);
using var activates = MediaFactory.MFTEnumEx(
    TransformCategoryGuids.VideoEncoder,
    (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter),
    new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 },
    new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 });

foreach (var act in activates)
{
    string name;
    try { name = act.GetString(mftFriendlyName); } catch { name = "?"; }

    IMFTransform transform;
    try
    {
        transform = act.ActivateObject<IMFTransform>();
        Console.WriteLine($"encoder MFT: {name}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"encoder MFT: {name} (activation failed: {HResult(ex)})");
        continue;
    }
    try
    {
        transform.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
    }
    catch { /* sync */ }

    transform.SetOutputType(0, VideoType(VideoFormatGuids.H264), 0);
    transform.SetInputType(0, VideoType(VideoFormatGuids.NV12), 0);

    using var manager = MediaFactory.MFCreateDXGIDeviceManager();
    manager.ResetDevice(device!);
    try
    {
        transform.ProcessMessage(TMessageType.MessageSetD3DManager,
            new UIntPtr((ulong)manager.NativePointer.ToInt64()));
        Console.WriteLine("  SetD3DManager: ok");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  SetD3DManager: FAILED {HResult(ex)}");
    }

    foreach (var (vname, tex) in passedTextures)
    {
        using var sample = MediaFactory.MFCreateSample();
        using var surface = MediaFactory.MFCreateDXGISurfaceBuffer(texture2DIid, tex, 0, new RawBool(false));
        sample.AddBuffer(surface);
        sample.SampleTime = 0;
        sample.SampleDuration = 333_333;
        try
        {
            transform.ProcessInput(0, sample, 0);
            Console.WriteLine($"  ProcessInput [{vname,-20}]: OK");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ProcessInput [{vname,-20}]: FAILED {HResult(ex)}");
        }
    }

    transform.Dispose();
    break; // first encoder is enough
}

Console.WriteLine("VERDICT: see per-variant results above.");

static IMFMediaType VideoType(Guid subtype) => WithSubtype(subtype);
static IMFMediaType WithSubtype(Guid subtype)
{
    var type = MediaFactory.MFCreateMediaType();
    type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
    type.Set(MediaTypeAttributeKeys.Subtype, subtype);
    type.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)W << 32) | (uint)H);
    type.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)30 << 32) | 1u);
    type.Set(MediaTypeAttributeKeys.PixelAspectRatio, ((ulong)1 << 32) | 1u);
    type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
    return type;
}

static string HResult(Exception ex) =>
    $"0x{ex.HResult:X8} {(ex.HResult == unchecked((int)0x80070057) ? "(E_INVALIDARG)" : "")} {ex.Message.Split('\n')[0]}";
