// Standalone probe: which NV12/P010 DXGI-surface form does this machine's hardware
// H.264 encoder MFT accept via ProcessInput? Each variant is tested with the full
// MFT ceremony in ffmpeg h264_mf's order (async unlock → CodecAPI-free negotiation →
// D3D manager → BeginStreaming/StartOfStream → NeedInput-gated ProcessInput → drain),
// because an async MFT may legitimately reject feeds outside that window and earlier
// probe versions reported false negatives without it.
// Usage: dotnet run --project tools/SurfaceProbe

using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

const int W = 1280, H = 720, FPS = 30;

var mftFriendlyName = new Guid("314ffbae-5b41-4c95-9c19-4e7d586face3");
var texture2DIid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c"); // IID_ID3D11Texture2D
var d3d11DeviceIid = new Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140"); // IID_ID3D11Device
// Not exposed by Vortice (values from mftransform.h via ffmpeg's mf_utils.h).
var mfSaD3d11Shared = new Guid("7b8f32c3-6d96-4b89-9203-dd38b61414f3");
var mfSaD3d11SharedWithoutMutex = new Guid("39dbd44d-2e44-4931-a4c8-352d3dc42115");

const int HrNoEventsAvailable = unchecked((int)0xC00D3E80); // MF_E_NO_EVENTS_AVAILABLE
const int HrNeedMoreInput = unchecked((int)0xC00D6D72);     // MF_E_TRANSFORM_NEED_MORE_INPUT
const int HrStreamChange = unchecked((int)0xC00D6D61);      // MF_E_TRANSFORM_STREAM_CHANGE
const int EventFlagNoWait = 1;                              // MF_EVENT_FLAG_NO_WAIT

MediaFactory.MFStartup(true);

var deviceNormal = CreateDevice(DeviceCreationFlags.None, "normal");
var deviceVideo = CreateDevice(DeviceCreationFlags.VideoSupport, "video-support");

// ---------------------------------------------------------------------------
// Inventory: what does the first hardware H.264 encoder look like?
// ---------------------------------------------------------------------------
using var activates = MediaFactory.MFTEnumEx(
    TransformCategoryGuids.VideoEncoder,
    (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter),
    new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 },
    new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 });

IMFActivate? firstActivate = null;
Console.WriteLine("=== hardware H.264 encoder MFTs ===");
foreach (var act in activates)
{
    string actName;
    try { actName = act.GetString(mftFriendlyName); } catch { actName = "?"; }
    Console.WriteLine($"  - {actName}");
    firstActivate ??= act;
}

if (firstActivate is null)
{
    Console.WriteLine("VERDICT: no hardware H.264 encoder MFT at all; CPU path only.");
    return;
}

string encoderName;
try { encoderName = firstActivate.GetString(mftFriendlyName); } catch { encoderName = "?"; }
Console.WriteLine($"=== probing: {encoderName} ===");

using (var inventory = CreateConfigured(deviceNormal, VideoFormatGuids.NV12, false, out var invManager))
using (invManager)
{
    PrintAttr(inventory, TransformAttributeKeys.D3D11Aware, "MF_SA_D3D11_AWARE");
    PrintAttr(inventory, TransformAttributeKeys.D3D11Bindflags, "MF_SA_D3D11_BINDFLAGS");
    PrintAttr(inventory, TransformAttributeKeys.D3D11Usage, "MF_SA_D3D11_USAGE");
    PrintAttr(inventory, mfSaD3d11Shared, "MF_SA_D3D11_SHARED");
    PrintAttr(inventory, mfSaD3d11SharedWithoutMutex, "MF_SA_D3D11_SHARED_WITHOUT_MUTEX");

    var inInfo = inventory.GetInputStreamInfo(0);
    Console.WriteLine($"  input stream info:  flags=0x{inInfo.Flags:X} size={inInfo.Size} align={inInfo.Alignment}");
    var outInfo = inventory.GetOutputStreamInfo(0);
    Console.WriteLine($"  output stream info: flags=0x{outInfo.Flags:X} size={outInfo.Size} align={outInfo.Alignment}");

    try
    {
        var inIds = new int[1];
        var outIds = new int[1];
        inventory.GetStreamIDs(1, inIds, 1, outIds);
        Console.WriteLine($"  stream ids: input={inIds[0]} output={outIds[0]}");
    }
    catch
    {
        Console.WriteLine("  stream ids: E_NOTIMPL (defaults 0/0)");
    }

    // Does the MFT keep our device in the manager, or swap in its own?
    invManager.OpenDeviceHandle(out var handle);
    invManager.GetVideoService(handle, d3d11DeviceIid, out var devicePtr);
    invManager.CloseDeviceHandle(handle);
    var swapped = devicePtr != deviceNormal.NativePointer;
    Console.WriteLine($"  manager device after SetD3DManager: {(swapped ? "SWAPPED by the MFT (input textures must live on its device)" : "ours (unchanged)")}");
    if (devicePtr != IntPtr.Zero) Marshal.Release(devicePtr);

    // IMFVideoSampleAllocator is the vendor-sanctioned way to get MFT-approved surfaces.
    try
    {
        using var allocator = inventory.QueryInterface<IMFVideoSampleAllocator>();
        Console.WriteLine("  IMFVideoSampleAllocator: PRESENT");
    }
    catch
    {
        Console.WriteLine("  IMFVideoSampleAllocator: absent");
    }
}

// ---------------------------------------------------------------------------
// Feed-test matrix. Every variant gets a fresh MFT with the full ceremony.
// ---------------------------------------------------------------------------
var variants = new (string Name, Format Fmt, Guid Subtype, BindFlags Bind, ResourceOptionFlags Misc,
                     ID3D11Device Device, bool VideoSample, bool OfferedInput)[]
{
    ("NV12 bind=0 misc=0",            Format.NV12, VideoFormatGuids.NV12, 0, 0, deviceNormal, false, false),
    ("NV12 RT",                       Format.NV12, VideoFormatGuids.NV12, BindFlags.RenderTarget, 0, deviceNormal, false, false),
    ("NV12 RT|SRV",                   Format.NV12, VideoFormatGuids.NV12, BindFlags.RenderTarget | BindFlags.ShaderResource, 0, deviceNormal, false, false),
    ("NV12 RT Shared",                Format.NV12, VideoFormatGuids.NV12, BindFlags.RenderTarget, ResourceOptionFlags.Shared, deviceNormal, false, false),
    ("NV12 RT SharedKeyedMutex",      Format.NV12, VideoFormatGuids.NV12, BindFlags.RenderTarget, ResourceOptionFlags.SharedKeyedMutex, deviceNormal, false, false),
    ("NV12 bind=0 Shared",            Format.NV12, VideoFormatGuids.NV12, 0, ResourceOptionFlags.Shared, deviceNormal, false, false),
    ("NV12 RT SharedNthandle",        Format.NV12, VideoFormatGuids.NV12, BindFlags.RenderTarget, ResourceOptionFlags.Shared | ResourceOptionFlags.SharedNTHandle, deviceNormal, false, false),
    ("NV12 VideoEncoder-only",        Format.NV12, VideoFormatGuids.NV12, BindFlags.VideoEncoder, 0, deviceNormal, false, false),
    ("NV12 RT (video device)",        Format.NV12, VideoFormatGuids.NV12, BindFlags.RenderTarget, 0, deviceVideo, false, false),
    ("NV12 bind=0 (video device)",    Format.NV12, VideoFormatGuids.NV12, 0, 0, deviceVideo, false, false),
    ("NV12 RT + VideoSampleFromSurface", Format.NV12, VideoFormatGuids.NV12, BindFlags.RenderTarget, 0, deviceNormal, true, false),
    ("NV12 bind=0 + VideoSampleFromSurface", Format.NV12, VideoFormatGuids.NV12, 0, 0, deviceNormal, true, false),
    ("NV12 RT + offered input type",  Format.NV12, VideoFormatGuids.NV12, BindFlags.RenderTarget, 0, deviceNormal, false, true),
    ("P010 RT",                       Format.P010, VideoFormatGuids.P010, BindFlags.RenderTarget, 0, deviceNormal, false, false),
    ("P010 bind=0",                   Format.P010, VideoFormatGuids.P010, 0, 0, deviceNormal, false, false),
};

Console.WriteLine("=== feed tests (full ceremony per variant) ===");
var anyAccepted = false;
foreach (var v in variants)
{
    RunFeedTest(v.Name, v.Fmt, v.Subtype, v.Bind, v.Misc, v.Device, v.VideoSample, v.OfferedInput, ref anyAccepted);
}

// ---------------------------------------------------------------------------
// Discriminators: does the manager itself poison input? Does LockDevice help?
// Do feature levels matter? Is memory input (the known-good path) intact here?
// ---------------------------------------------------------------------------
Console.WriteLine("=== discriminator tests ===");
RunMemoryFeedTest("memory buffer, NO manager (sanity)", false, ref anyAccepted);
RunMemoryFeedTest("memory buffer, WITH manager", true, ref anyAccepted);
RunLockedSurfaceTest(ref anyAccepted);
using (var deviceFl = CreateDevice(DeviceCreationFlags.None, "FL 12_0/11_1/11_0",
           new[] { FeatureLevel.Level_12_0, FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }))
{
    RunFeedTest("NV12 RT (broad-FL device)", Format.NV12, VideoFormatGuids.NV12,
        BindFlags.RenderTarget, 0, deviceFl, false, false, ref anyAccepted);
}

// Allocator-allocated surface: if the MFT hands us the surface itself, it cannot
// complain about its form.
Console.WriteLine("=== IMFVideoSampleAllocator path ===");
RunAllocatorTest(deviceNormal, ref anyAccepted);

Console.WriteLine(anyAccepted
    ? "VERDICT: at least one surface form is accepted; see matrix above."
    : "VERDICT: no surface form accepted — this encoder takes system-memory input only.");

// ---------------------------------------------------------------------------

static ID3D11Device CreateDevice(DeviceCreationFlags flags, string label, FeatureLevel[]? levels = null)
{
    using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
    factory.EnumAdapters(0, out var adapter).CheckError();
    using (adapter)
    {
        Console.WriteLine($"device ({label}): {adapter.Description.Description.TrimEnd('\0')}");
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, flags,
            levels ?? new[] { FeatureLevel.Level_11_0 }, out ID3D11Device? device, out ID3D11DeviceContext? _)
            .CheckError();
        return device!;
    }
}

static void PrintAttr(IMFTransform transform, Guid key, string name)
{
    try { Console.WriteLine($"  {name}: {transform.Attributes.GetUInt32(key)}"); }
    catch { Console.WriteLine($"  {name}: (absent)"); }
}

/// <summary>Creates and configures the hardware encoder exactly like ffmpeg's h264_mf
/// does up to StartOfStream. Caller disposes. Manager is returned for inspection.</summary>
static IMFTransform CreateConfigured(ID3D11Device device, Guid inputSubtype,
    bool offeredInput, out IMFDXGIDeviceManager manager, bool setManager = true)
{
    using var activates = MediaFactory.MFTEnumEx(
        TransformCategoryGuids.VideoEncoder,
        (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter),
        new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 },
        new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 });
    var transform = null as IMFTransform;
    foreach (var act in activates)
    {
        transform = act.ActivateObject<IMFTransform>();
        break;
    }

    if (transform is null)
        throw new InvalidOperationException("No hardware H.264 encoder MFT found.");

    try
    {
        try
        {
            if (transform.Attributes.GetUInt32(TransformAttributeKeys.TransformAsync) != 0)
                transform.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
        }
        catch { /* sync MFT */ }

        transform.SetOutputType(0, VideoType(VideoFormatGuids.H264, withBitrate: true), 0);

        IMFMediaType inputType;
        if (offeredInput)
        {
            // ffmpeg sets the MFT's own offered type unchanged (only validating the subtype).
            inputType = null!;
            for (var i = 0; ; i++)
            {
                IMFMediaType offered;
                try { offered = transform.GetInputAvailableType(0, i); }
                catch { break; }
                if (offered.GetGUID(MediaTypeAttributeKeys.Subtype) == inputSubtype)
                {
                    inputType = offered;
                    break;
                }
                offered.Dispose();
            }
            inputType ??= VideoType(inputSubtype, withBitrate: false);
        }
        else
        {
            inputType = VideoType(inputSubtype, withBitrate: false);
        }

        using (inputType)
            transform.SetInputType(0, inputType, 0);

        manager = null!;
        if (setManager)
        {
            manager = MediaFactory.MFCreateDXGIDeviceManager();
            manager.ResetDevice(device);
            transform.ProcessMessage(TMessageType.MessageSetD3DManager,
                new UIntPtr((ulong)manager.NativePointer.ToInt64()));
        }

        transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        return transform;
    }
    catch
    {
        transform.Dispose();
        throw;
    }
}

void RunFeedTest(string name, Format format, Guid subtype, BindFlags bind, ResourceOptionFlags misc,
    ID3D11Device device, bool videoSample, bool offeredInput, ref bool anyAccepted)
{
    ID3D11Texture2D? texture = null;
    IMFTransform? transform = null;
    IMFDXGIDeviceManager? manager = null;
    var stage = "texture creation";
    try
    {
        texture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = W, Height = H, MipLevels = 1, ArraySize = 1, Format = format,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = bind, CPUAccessFlags = CpuAccessFlags.None, MiscFlags = misc,
        });

        stage = "configure";
        transform = CreateConfigured(device, subtype, offeredInput, out manager);

        stage = "sample creation";
        using var sample = videoSample
            ? CreateVideoSample(texture)
            : CreateSurfaceSample(texture);
        sample.SampleTime = 0;
        sample.SampleDuration = 333_333;
        // ffmpeg marks the first sample as a discontinuity.
        sample.Set(SampleAttributeKeys.Discontinuity, 1u);

        stage = "need-input wait";
        WaitForEvent(transform, MediaEventTypes.TransformNeedInput, 3000);

        stage = "process input";
        transform.ProcessInput(0, sample, 0);

        stage = "drain";
        var (accessUnits, bytes) = Drain(transform);
        anyAccepted = true;
        Console.WriteLine($"  [OK]   {name,-38} fed; drain produced {accessUnits} AU(s), {bytes} byte(s)");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [FAIL] {name,-38} {stage}: 0x{ex.HResult:X8} {FirstLine(ex.Message)}");
    }
    finally
    {
        texture?.Dispose();
        manager?.Dispose();
        transform?.Dispose();
    }
}

void RunAllocatorTest(ID3D11Device device, ref bool anyAccepted)
{
    IMFTransform? transform = null;
    IMFDXGIDeviceManager? manager = null;
    var stage = "configure";
    try
    {
        transform = CreateConfigured(device, VideoFormatGuids.NV12, false, out manager);

        stage = "QI IMFVideoSampleAllocator";
        using var allocator = transform.QueryInterface<IMFVideoSampleAllocator>();

        stage = "initialize allocator";
        using (var inputType = VideoType(VideoFormatGuids.NV12, withBitrate: false))
        {
            allocator.SetDirectXManager(manager);
            allocator.InitializeSampleAllocator(4, inputType);
        }

        stage = "allocate sample";
        using var sample = allocator.AllocateSample();

        // Fill the allocated surface through our own device so the content is defined.
        stage = "fill surface";
        using var source = device.CreateTexture2D(new Texture2DDescription
        {
            Width = W, Height = H, MipLevels = 1, ArraySize = 1, Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = 0, CPUAccessFlags = CpuAccessFlags.None, MiscFlags = 0,
        });
        using var buffer = sample.GetBufferByIndex(0);
        using var dxgiBuffer = buffer.QueryInterface<IMFDXGIBuffer>();
        using var target = new ID3D11Texture2D(dxgiBuffer.GetResource(texture2DIid));
        using (var context = device.ImmediateContext)
            context.CopyResource(target, source);

        stage = "need-input wait";
        sample.SampleTime = 0;
        sample.SampleDuration = 333_333;
        sample.Set(SampleAttributeKeys.Discontinuity, 1u);
        WaitForEvent(transform, MediaEventTypes.TransformNeedInput, 3000);

        stage = "process input";
        transform.ProcessInput(0, sample, 0);

        stage = "drain";
        var (accessUnits, bytes) = Drain(transform);
        anyAccepted = true;
        Console.WriteLine($"  [OK]   allocator-allocated surface fed; drain produced {accessUnits} AU(s), {bytes} byte(s)");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [FAIL] allocator path {stage}: 0x{ex.HResult:X8} {FirstLine(ex.Message)}");
    }
    finally
    {
        manager?.Dispose();
        transform?.Dispose();
    }
}

void RunMemoryFeedTest(string name, bool setManager, ref bool anyAccepted)
{
    IMFTransform? transform = null;
    IMFDXGIDeviceManager? manager = null;
    var stage = "configure";
    try
    {
        transform = CreateConfigured(deviceNormal, VideoFormatGuids.NV12, false, out manager, setManager);

        stage = "sample creation";
        var size = W * H * 3 / 2;
        using var sample = MediaFactory.MFCreateSample();
        using var buffer = MediaFactory.MFCreateAlignedMemoryBuffer(size, 15);
        buffer.CurrentLength = size;
        sample.AddBuffer(buffer);
        sample.SampleTime = 0;
        sample.SampleDuration = 333_333;
        sample.Set(SampleAttributeKeys.Discontinuity, 1u);

        stage = "need-input wait";
        WaitForEvent(transform, MediaEventTypes.TransformNeedInput, 3000);

        stage = "process input";
        transform.ProcessInput(0, sample, 0);

        stage = "drain";
        var (accessUnits, bytes) = Drain(transform);
        anyAccepted = true;
        Console.WriteLine($"  [OK]   {name,-42} fed; drain produced {accessUnits} AU(s), {bytes} byte(s)");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [FAIL] {name,-42} {stage}: 0x{ex.HResult:X8} {FirstLine(ex.Message)}");
    }
    finally
    {
        manager?.Dispose();
        transform?.Dispose();
    }
}

void RunLockedSurfaceTest(ref bool anyAccepted)
{
    ID3D11Texture2D? texture = null;
    IMFTransform? transform = null;
    IMFDXGIDeviceManager? manager = null;
    var stage = "configure";
    try
    {
        texture = deviceNormal.CreateTexture2D(new Texture2DDescription
        {
            Width = W, Height = H, MipLevels = 1, ArraySize = 1, Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget, CPUAccessFlags = CpuAccessFlags.None, MiscFlags = 0,
        });
        transform = CreateConfigured(deviceNormal, VideoFormatGuids.NV12, false, out manager);

        stage = "need-input wait";
        using var sample = CreateSurfaceSample(texture);
        sample.SampleTime = 0;
        sample.SampleDuration = 333_333;
        sample.Set(SampleAttributeKeys.Discontinuity, 1u);
        WaitForEvent(transform, MediaEventTypes.TransformNeedInput, 3000);

        // Some vendor MFTs only accept surfaces while the caller holds the manager's
        // device lock, synchronizing access to the shared device.
        stage = "process input (device locked)";
        manager.OpenDeviceHandle(out var handle);
        ID3D11Device? locked = null;
        try
        {
            locked = manager.LockDevice<ID3D11Device>(handle, true);
            transform.ProcessInput(0, sample, 0);
        }
        finally
        {
            locked?.Dispose();
            manager.UnlockDevice(handle);
            manager.CloseDeviceHandle(handle);
        }

        stage = "drain";
        var (accessUnits, bytes) = Drain(transform);
        anyAccepted = true;
        Console.WriteLine($"  [OK]   {"NV12 RT + LockDevice",-42} fed; drain produced {accessUnits} AU(s), {bytes} byte(s)");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [FAIL] {"NV12 RT + LockDevice",-42} {stage}: 0x{ex.HResult:X8} {FirstLine(ex.Message)}");
    }
    finally
    {
        texture?.Dispose();
        manager?.Dispose();
        transform?.Dispose();
    }
}

IMFSample CreateSurfaceSample(ID3D11Texture2D texture)
{
    var sample = MediaFactory.MFCreateSample();
    using var surface = MediaFactory.MFCreateDXGISurfaceBuffer(texture2DIid, texture, 0, new RawBool(false));
    sample.AddBuffer(surface);
    return sample;
}

static IMFSample CreateVideoSample(ID3D11Texture2D texture)
{
    MediaFactory.MFCreateVideoSampleFromSurface(texture, out var sample);
    return sample;
}

static void WaitForEvent(IMFTransform transform, MediaEventTypes wanted, int timeoutMs)
{
    using var events = transform.QueryInterface<IMFMediaEventGenerator>();
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < timeoutMs)
    {
        IMFMediaEvent? mediaEvent = null;
        try
        {
            mediaEvent = events.GetEvent(EventFlagNoWait);
            if (mediaEvent.EventType == wanted)
                return;
        }
        catch (SharpGenException ex) when (ex.HResult == HrNoEventsAvailable)
        {
            Thread.Sleep(5);
        }
        finally
        {
            mediaEvent?.Dispose();
        }
    }
    throw new InvalidOperationException($"timed out waiting for {wanted}");
}

/// <summary>Drains the encoder after the single fed frame; returns AU count and total bytes.</summary>
static (int AccessUnits, long Bytes) Drain(IMFTransform transform)
{
    transform.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);

    var outInfo = transform.GetOutputStreamInfo(0);
    var providesSamples = (outInfo.Flags & 0x300) != 0; // PROVIDES or CAN_PROVIDE
    var bufferSize = Math.Max(outInfo.Size, 1 << 20);

    using var events = transform.QueryInterface<IMFMediaEventGenerator>();
    var accessUnits = 0;
    long bytes = 0;
    var sw = Stopwatch.StartNew();
    var done = false;
    while (!done && sw.ElapsedMilliseconds < 5000)
    {
        IMFMediaEvent? mediaEvent = null;
        try
        {
            mediaEvent = events.GetEvent(EventFlagNoWait);
            switch (mediaEvent.EventType)
            {
                case MediaEventTypes.TransformHaveOutput:
                    PullOutput(transform, providesSamples, bufferSize, ref accessUnits, ref bytes);
                    break;
                case MediaEventTypes.TransformDrainComplete:
                    done = true;
                    break;
            }
        }
        catch (SharpGenException ex) when (ex.HResult == HrNoEventsAvailable)
        {
            Thread.Sleep(5);
        }
        finally
        {
            mediaEvent?.Dispose();
        }
    }

    // Sync MFTs (and stragglers) yield output on direct pulls.
    for (var i = 0; i < 8; i++)
        if (!PullOutput(transform, providesSamples, bufferSize, ref accessUnits, ref bytes))
            break;

    return (accessUnits, bytes);
}

static bool PullOutput(IMFTransform transform, bool providesSamples, int bufferSize,
    ref int accessUnits, ref long bytes)
{
    var buf = new OutputDataBuffer { StreamID = 0 };
    IMFSample? allocated = null;
    IMFMediaBuffer? allocatedBuffer = null;
    if (!providesSamples)
    {
        allocated = MediaFactory.MFCreateSample();
        allocatedBuffer = MediaFactory.MFCreateAlignedMemoryBuffer(bufferSize, 15);
        allocated.AddBuffer(allocatedBuffer);
        buf.Sample = allocated;
    }

    var result = transform.ProcessOutput(0, 1, ref buf, out _);
    if (result.Code == HrNeedMoreInput)
    {
        allocatedBuffer?.Dispose();
        allocated?.Dispose();
        return false;
    }

    if (result.Code == HrStreamChange)
    {
        allocatedBuffer?.Dispose();
        allocated?.Dispose();
        transform.SetOutputType(0, transform.GetOutputAvailableType(0, 0), 0);
        return true;
    }

    result.CheckError();

    var sample = buf.Sample!;
    using (var contiguous = sample.ConvertToContiguousBuffer())
    {
        contiguous.Lock(out _, out _, out var length);
        bytes += length;
        if (length > 0)
            accessUnits++;
        contiguous.Unlock();
    }

    sample.Dispose();
    allocatedBuffer?.Dispose();
    return true;
}

static IMFMediaType VideoType(Guid subtype, bool withBitrate)
{
    var type = MediaFactory.MFCreateMediaType();
    type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
    type.Set(MediaTypeAttributeKeys.Subtype, subtype);
    type.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)W << 32) | (uint)H);
    type.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)FPS << 32) | 1u);
    type.Set(MediaTypeAttributeKeys.PixelAspectRatio, ((ulong)1 << 32) | 1u);
    type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u); // progressive
    if (withBitrate)
        type.Set(MediaTypeAttributeKeys.AvgBitrate, 8_000_000u);
    return type;
}

static string FirstLine(string message) => message.Split('\n')[0];
