using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace RemoteController.Client.Services;

/// <summary>
/// H.264 decoder backed by a MediaFoundation MFT, hardware preferred. Microsoft's software
/// decoder keeps dozens of frames in flight (seconds of display lag); hardware decoders
/// hold only a few. Async MFTs are driven like ffmpeg's h264_mf: MF_TRANSFORM_ASYNC_UNLOCK
/// via the MFT's attribute store, then ProcessInput/ProcessOutput gated on MFT events.
/// Windows 10+ only.
/// </summary>
public sealed class H264Decoder : IDisposable
{
    private const int HrNotAccepting = unchecked((int)0xC00D36B5);      // MF_E_NOTACCEPTING
    private const int HrNeedMoreInput = unchecked((int)0xC00D6D72);     // MF_E_TRANSFORM_NEED_MORE_INPUT
    private const int HrAsyncLocked = unchecked((int)0xC00D6D77);       // MF_E_TRANSFORM_ASYNC_LOCKED
    private const int HrNoEventsAvailable = unchecked((int)0xC00D3E80); // MF_E_NO_EVENTS_AVAILABLE
    private const int HrStreamChange = unchecked((int)0xC00D6D61);      // MF_E_TRANSFORM_STREAM_CHANGE
    private const int EventFlagNoWait = 1;                              // MF_EVENT_FLAG_NO_WAIT

    // MFT_FRIENDLY_NAME_Attribute
    private static readonly Guid MftFriendlyNameKey = new("314ffbae-5b41-4c95-9c19-4e7d586face3");

    private static readonly object MfStartupLock = new();
    private static bool _mfStarted;

    private readonly IMFTransform _transform;
    private readonly IMFMediaEventGenerator? _events;
    private readonly int _outputBufferSize;
    private readonly bool _outputProvidesSamples;
    private int _alignedWidth;
    private int _alignedHeight;
    private bool _needInputPending;

    public H264Decoder(int width, int height)
    {
        lock (MfStartupLock)
        {
            if (!_mfStarted)
            {
                MediaFactory.MFStartup(true);
                _mfStarted = true;
            }
        }

        (_transform, _events, OutputIsBgra) = CreateTransform(width, height);

        var info = _transform.GetOutputStreamInfo(0);
        _outputBufferSize = Math.Max(info.Size, 1);
        _outputProvidesSamples = (info.Flags & 0x100) != 0; // MFT_OUTPUT_STREAM_PROVIDES_SAMPLES
        UpdateAlignedSize();
    }

    /// <summary>True when the decoder outputs BGRA directly; false for NV12 output.</summary>
    public bool OutputIsBgra { get; private set; }

    /// <summary>
    /// Feeds one access unit. <paramref name="onFrame"/> fires once per decoded frame
    /// (presentation order), with the pointer valid only for the duration of the callback.
    /// Arguments: data pointer, stride, aligned width, aligned height — crop to the display
    /// size, the output may be macroblock-padded (e.g. 1920x1088 for 1920x1080).
    /// </summary>
    public void Decode(byte[] accessUnit, long timestamp, Action<IntPtr, int, int, int> onFrame)
    {
        using var sample = MediaFactory.MFCreateSample();
        using var buffer = MediaFactory.MFCreateMemoryBuffer(accessUnit.Length);
        buffer.Lock(out var ptr, out _, out _);
        Marshal.Copy(accessUnit, 0, ptr, accessUnit.Length);
        buffer.CurrentLength = accessUnit.Length;
        buffer.Unlock();
        sample.AddBuffer(buffer);
        sample.SampleTime = timestamp;

        if (_events is not null)
        {
            FeedEventDriven(sample, onFrame);
            return;
        }

        while (true)
        {
            try
            {
                _transform.ProcessInput(0, sample, 0);
                break;
            }
            catch (SharpGenException ex) when (ex.HResult == HrNotAccepting)
            {
                Drain(onFrame);
            }
        }

        Drain(onFrame);
    }

    public void Dispose() => _transform.Dispose();

    /// <summary>Waits for the decoder's NeedInput event, feeds the sample, pulls available output.</summary>
    private void FeedEventDriven(IMFSample sample, Action<IntPtr, int, int, int> onFrame)
    {
        for (var spins = 0; !_needInputPending; spins++)
        {
            if (spins >= 2000)
                throw new InvalidOperationException("H.264 decoder stopped producing events.");
            if (TryGetEvent(out var mediaEvent))
            {
                using (mediaEvent)
                {
                    if (HandleEvent(mediaEvent, onFrame))
                        break;
                }
            }
            else
            {
                Thread.Sleep(1);
            }
        }

        _needInputPending = false;
        _transform.ProcessInput(0, sample, 0);

        DrainEvents(onFrame);
    }

    /// <summary>Consumes pending MFT events without blocking.</summary>
    private void DrainEvents(Action<IntPtr, int, int, int> onFrame)
    {
        while (TryGetEvent(out var mediaEvent))
        {
            using (mediaEvent)
                HandleEvent(mediaEvent, onFrame);
        }
    }

    /// <summary>Handles one MFT event; returns true when it was a NeedInput request.</summary>
    private bool HandleEvent(IMFMediaEvent mediaEvent, Action<IntPtr, int, int, int> onFrame)
    {
        switch (mediaEvent.EventType)
        {
            case MediaEventTypes.TransformNeedInput:
                _needInputPending = true;
                return true;
            case MediaEventTypes.TransformHaveOutput:
                // Event-gated MFTs expect exactly one ProcessOutput per HaveOutput event.
                DrainOnce(onFrame);
                return false;
            default:
                return false;
        }
    }

    private bool TryGetEvent(out IMFMediaEvent mediaEvent)
    {
        try
        {
            mediaEvent = _events!.GetEvent(EventFlagNoWait);
            return true;
        }
        catch (SharpGenException ex) when (ex.HResult == HrNoEventsAvailable)
        {
            mediaEvent = null!;
            return false;
        }
    }

    private static (IMFTransform Transform, IMFMediaEventGenerator? Events, bool IsBgra) CreateTransform(int width, int height)
    {
        // Hardware decoder MFTs only show up with the HARDWARE flag; fall back to anything
        // (Microsoft's software decoder) if none are available or usable.
        return TryCreateTransform(width, height,
                   (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter), "hardware")
               ?? TryCreateTransform(width, height,
                   (uint)EnumFlag.EnumFlagSortandfilter, "any")
               ?? throw new InvalidOperationException("No H.264 decoder MFT found on this system.");
    }

    private static (IMFTransform, IMFMediaEventGenerator?, bool)? TryCreateTransform(int width, int height, uint flags, string label)
    {
        using var activates = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoDecoder,
            flags,
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 },
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 });

        foreach (var act in activates)
        {
            string name;
            try { name = act.GetString(MftFriendlyNameKey); } catch { name = "?"; }
            Console.WriteLine($"[Client] H.264 decoder MFT ({label}): {name}");
            try
            {
                var transform = act.ActivateObject<IMFTransform>();
                try
                {
                    var (events, isBgra) = Configure(transform, width, height);
                    Console.WriteLine($"[Client] using decoder: {name}");
                    return (transform, events, isBgra);
                }
                catch
                {
                    transform.Dispose();
                    throw;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Client] decoder unusable: {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>Unlocks async MFTs and negotiates types (ffmpeg's order).</summary>
    private static (IMFMediaEventGenerator? Events, bool IsBgra) Configure(IMFTransform transform, int width, int height)
    {
        var isAsync = false;
        try { isAsync = transform.Attributes.GetUInt32(TransformAttributeKeys.TransformAsync) != 0; }
        catch { /* attribute absent on sync MFTs */ }

        IMFMediaEventGenerator? events = null;
        if (isAsync)
        {
            transform.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
            events = transform.QueryInterface<IMFMediaEventGenerator>();
            Console.WriteLine("[Client] decoder is async; using event-driven pumping.");
        }

        // Minimize the decoder's internal pipeline depth; the stream is change-driven, so
        // a deep frame buffer becomes many seconds of display lag at low frame rates.
        CodecApi.TrySet(transform, CodecApi.LowLatencyMode, true, "low-latency mode");
        CodecApi.TrySet(transform, CodecApi.FastDecodeMode, true, "fast decode mode");

        var inputType = MediaFactory.MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        inputType.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
        transform.SetInputType(0, inputType, 0);

        var (outputType, isBgra) = PickOutputType(transform);
        transform.SetOutputType(0, outputType, 0);

        transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        return (events, isBgra);
    }

    /// <summary>
    /// Chooses the decoder's output format: RGB32 (BGRA) when offered — the MFT then does
    /// the color conversion in optimized native code and the client skips its slowest
    /// stage — otherwise NV12 (converted on the client).
    /// </summary>
    private static (IMFMediaType Type, bool IsBgra) PickOutputType(IMFTransform transform)
    {
        IMFMediaType? nv12 = null;
        for (var i = 0; ; i++)
        {
            IMFMediaType type;
            try { type = transform.GetOutputAvailableType(0, i); }
            catch { break; }

            var subtype = type.GetGUID(MediaTypeAttributeKeys.Subtype);
            if (subtype == VideoFormatGuids.Rgb32)
                return (type, true);

            nv12 ??= type;
        }

        return (nv12 ?? throw new InvalidOperationException("H.264 decoder offered no usable output types."), false);
    }

    /// <summary>Pulls output until the MFT reports it needs more input (sync pumping).</summary>
    private void Drain(Action<IntPtr, int, int, int> onFrame)
    {
        while (DrainOnce(onFrame))
        {
        }
    }

    /// <summary>One ProcessOutput attempt; returns true when a frame came out.</summary>
    private bool DrainOnce(Action<IntPtr, int, int, int> onFrame)
    {
        var buf = new OutputDataBuffer { StreamID = 0 };
        IMFSample? allocated = null;
        IMFMediaBuffer? allocatedBuffer = null;
        if (!_outputProvidesSamples)
        {
            allocated = MediaFactory.MFCreateSample();
            allocatedBuffer = MediaFactory.MFCreateMemoryBuffer(_outputBufferSize);
            allocated.AddBuffer(allocatedBuffer);
            buf.Sample = allocated;
        }

        var result = _transform.ProcessOutput(0, 1, ref buf, out _);
        if (result.Code == HrNeedMoreInput)
        {
            allocatedBuffer?.Dispose();
            allocated?.Dispose();
            return false;
        }

        if (result.Code == HrStreamChange)
        {
            // The decoder renegotiated the output format from the stream's SPS.
            allocatedBuffer?.Dispose();
            allocated?.Dispose();
            var (outputType, isBgra) = PickOutputType(_transform);
            _transform.SetOutputType(0, outputType, 0);
            OutputIsBgra = isBgra;
            UpdateAlignedSize();
            return false;
        }

        result.CheckError();

        var sample = buf.Sample!;
        using var contiguous = sample.ConvertToContiguousBuffer();
        // The planes are tightly packed at the aligned width; the callback converts
        // straight out of native memory, so no managed copy is needed.
        contiguous.Lock(out var data, out _, out _);
        onFrame(data, _alignedWidth, _alignedWidth, _alignedHeight);
        contiguous.Unlock();
        sample.Dispose();
        // The sample only held a reference; the buffer we created must go too.
        allocatedBuffer?.Dispose();
        return true;
    }

    private void UpdateAlignedSize()
    {
        var size = _transform.GetOutputCurrentType(0).GetUInt64(MediaTypeAttributeKeys.FrameSize);
        _alignedWidth = (int)(size >> 32);
        _alignedHeight = (int)(size & 0xFFFFFFFF);
    }

    private static ulong Pack(int hi, int lo) => ((ulong)hi << 32) | (uint)lo;
}
