using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace RemoteController.Host;

/// <summary>
/// H.264 encoder backed by a MediaFoundation MFT (hardware when installed, otherwise the
/// Microsoft software encoder). Consumes NV12 frames and emits Annex B access units.
/// Async MFTs (all hardware encoders) are driven the way ffmpeg's h264_mf does it:
/// MF_TRANSFORM_ASYNC_UNLOCK via the MFT's own attribute store, then ProcessInput /
/// ProcessOutput strictly gated on METransformNeedInput / METransformHaveOutput events.
/// Sync MFTs (Microsoft's software encoder) are pumped directly.
/// </summary>
public sealed class H264Encoder : IDisposable
{
    private const int HrNotAccepting = unchecked((int)0xC00D36B5);      // MF_E_NOTACCEPTING
    private const int HrNeedMoreInput = unchecked((int)0xC00D6D72);     // MF_E_TRANSFORM_NEED_MORE_INPUT
    private const int HrAsyncLocked = unchecked((int)0xC00D6D77);       // MF_E_TRANSFORM_ASYNC_LOCKED
    private const int HrNoEventsAvailable = unchecked((int)0xC00D3E80); // MF_E_NO_EVENTS_AVAILABLE
    private const int HrAttributeNotFound = unchecked((int)0xC00D36E6); // MF_E_ATTRIBUTENOTFOUND
    private const int HrStreamChange = unchecked((int)0xC00D6D61);      // MF_E_TRANSFORM_STREAM_CHANGE
    private const int EventFlagNoWait = 1;                              // MF_EVENT_FLAG_NO_WAIT

    // MFT_FRIENDLY_NAME_Attribute
    private static readonly Guid MftFriendlyNameKey = new("314ffbae-5b41-4c95-9c19-4e7d586face3");

    private static readonly object MfStartupLock = new();
    private static bool _mfStarted;

    private readonly IMFTransform _transform;
    private readonly IMFMediaEventGenerator? _events;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly uint _bitrate;
    private int _outputBufferSize;
    private bool _outputProvidesSamples;
    private bool _needInputPending;
    private int _pendingFrames;
    private int _droppedFrames;

    // Beyond this backlog the encoder is not keeping up; frames get dropped instead of
    // piling into the MFT's internal queue (some MFTs accept input without bound, which
    // grows memory and latency without limit).
    private const int MaxPendingFrames = 8;

    public H264Encoder(int width, int height, int fps, uint bitrate)
    {
        lock (MfStartupLock)
        {
            if (!_mfStarted)
            {
                MediaFactory.MFStartup(true);
                _mfStarted = true;
            }
        }

        _width = width;
        _height = height;
        _fps = fps;
        _bitrate = bitrate;
        (_transform, _events) = CreateTransform(width, height, fps, bitrate);

        RefreshOutputInfo();
        SequenceHeader = ReadSequenceHeader();
    }

    /// <summary>
    /// SPS/PPS in Annex B form to precede the first keyframe sent to a decoder,
    /// or empty when the MFT doesn't expose one (those carry SPS/PPS in-band).
    /// </summary>
    public byte[] SequenceHeader { get; private set; }

    private void RefreshOutputInfo()
    {
        try
        {
            var info = _transform.GetOutputStreamInfo(0);
            _outputBufferSize = Math.Max(info.Size, 1);
            _outputProvidesSamples = (info.Flags & 0x100) != 0; // MFT_OUTPUT_STREAM_PROVIDES_SAMPLES
        }
        catch (SharpGenException ex) when (ex.HResult == HrAsyncLocked)
        {
            _outputBufferSize = 1 << 20; // conservative guess; async MFTs usually provide their own samples
            _outputProvidesSamples = true;
        }
    }

    /// <summary>Feeds one NV12 frame and returns the access units the encoder emitted for it.</summary>
    public List<(byte[] Data, bool Keyframe)> Encode(byte[] nv12, long timestamp100ns, long duration100ns)
    {
        var output = new List<(byte[] Data, bool Keyframe)>();

        if (_pendingFrames >= MaxPendingFrames)
        {
            // Sync MFTs (Microsoft's software encoder) accept input without bound and would
            // grow memory and latency forever, so cap them by dropping frames. Event-driven
            // MFTs bound their own input queue via NeedInput — starving those only risks a
            // deadlock, so just pull whatever output is available and keep feeding.
            if (_events is null)
            {
                Drain(output);
                if (++_droppedFrames % 300 == 1)
                    Console.WriteLine($"[Host] encoder is behind ({_pendingFrames} frames queued); dropped {_droppedFrames} frames so far");
                return output;
            }

            DrainEvents(output);
        }

        using var sample = MediaFactory.MFCreateSample();
        using var buffer = MediaFactory.MFCreateAlignedMemoryBuffer(nv12.Length, 15);
        buffer.Lock(out var ptr, out _, out _);
        Marshal.Copy(nv12, 0, ptr, nv12.Length);
        buffer.CurrentLength = nv12.Length;
        buffer.Unlock();
        sample.AddBuffer(buffer);
        sample.SampleTime = timestamp100ns;
        sample.SampleDuration = duration100ns;

        if (_events is not null)
        {
            FeedEventDriven(sample, output);
            return output;
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
                // Input queue is full; pull output first, then retry the same sample.
                Drain(output);
                Thread.Sleep(1);
            }
        }

        _pendingFrames++;
        Drain(output);
        return output;
    }

    public void Dispose() => _transform.Dispose();

    /// <summary>Waits for the encoder's NeedInput event, feeds the sample, pulls available output.</summary>
    private void FeedEventDriven(IMFSample sample, List<(byte[] Data, bool Keyframe)> output)
    {
        for (var spins = 0; !_needInputPending; spins++)
        {
            if (spins >= 5000)
            {
                // The encoder is slow or stalled (e.g. rate control choking on motion).
                // Drop the frame and move on; killing the session only freezes the picture.
                if (++_droppedFrames % 100 == 1)
                    Console.WriteLine($"[Host] encoder not accepting input; dropped {_droppedFrames} frames so far");
                return;
            }

            if (TryGetEvent(out var mediaEvent))
            {
                using (mediaEvent)
                {
                    if (HandleEvent(mediaEvent, output))
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
        _pendingFrames++;

        DrainEvents(output);
    }

    /// <summary>Consumes pending MFT events without blocking.</summary>
    private void DrainEvents(List<(byte[] Data, bool Keyframe)> output)
    {
        while (TryGetEvent(out var mediaEvent))
        {
            using (mediaEvent)
                HandleEvent(mediaEvent, output);
        }
    }

    /// <summary>Handles one MFT event; returns true when it was a NeedInput request.</summary>
    private bool HandleEvent(IMFMediaEvent mediaEvent, List<(byte[] Data, bool Keyframe)> output)
    {
        switch (mediaEvent.EventType)
        {
            case MediaEventTypes.TransformNeedInput:
                _needInputPending = true;
                return true;
            case MediaEventTypes.TransformHaveOutput:
                // Event-gated MFTs expect exactly one ProcessOutput per HaveOutput event.
                DrainOnce(output);
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

    private static (IMFTransform Transform, IMFMediaEventGenerator? Events) CreateTransform(int width, int height, int fps, uint bitrate)
    {
        // Hardware MFTs (QCOM/Intel/AMD/NVIDIA) only show up when the HARDWARE flag is
        // set, so enumerate twice: hardware first, then anything (software fallback).
        return TryCreateTransform(width, height, fps, bitrate,
                   (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter), "hardware")
               ?? TryCreateTransform(width, height, fps, bitrate,
                   (uint)EnumFlag.EnumFlagSortandfilter, "any")
               ?? throw new InvalidOperationException("No H.264 encoder MFT found on this system.");
    }

    private static (IMFTransform, IMFMediaEventGenerator?)? TryCreateTransform(int width, int height, int fps, uint bitrate, uint flags, string label)
    {
        using var activates = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoEncoder,
            flags,
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 },
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 });

        foreach (var act in activates)
        {
            string name;
            try { name = act.GetString(MftFriendlyNameKey); } catch { name = "?"; }
            Console.WriteLine($"[Host] H.264 encoder MFT ({label}): {name}");
            try
            {
                var transform = act.ActivateObject<IMFTransform>();
                try
                {
                    var events = Configure(transform, width, height, fps, bitrate);
                    Console.WriteLine($"[Host] using encoder: {name}");
                    return (transform, events);
                }
                catch
                {
                    transform.Dispose();
                    throw;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Host] encoder unusable: {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>Unlocks async MFTs, negotiates types, starts streaming (ffmpeg's order).</summary>
    private static IMFMediaEventGenerator? Configure(IMFTransform transform, int width, int height, int fps, uint bitrate)
    {
        // MF_TRANSFORM_ASYNC marks async MFTs; it is absent on sync ones.
        var isAsync = false;
        try { isAsync = transform.Attributes.GetUInt32(TransformAttributeKeys.TransformAsync) != 0; }
        catch { /* attribute absent on sync MFTs */ }

        IMFMediaEventGenerator? events = null;
        if (isAsync)
        {
            // The unlock lives on the MFT's own attribute store (IMFTransform::GetAttributes,
            // NOT QueryInterface<IMFAttributes>) and is required before type negotiation.
            // Even unlocked, ProcessXxx calls stay legal only in response to MFT events.
            transform.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
            events = transform.QueryInterface<IMFMediaEventGenerator>();
            Console.WriteLine("[Host] encoder is async; using event-driven pumping.");
        }

        // Latency-critical settings must go on before type negotiation (same as ffmpeg),
        // and Qualcomm's encoder is known to mishandle its default B-frame count.
        // (Rate-control knobs are deliberately left out: accepting LowDelayVBR makes
        // QCOM's subsequent SetOutputType fail with E_INVALIDARG.)
        CodecApi.TrySet(transform, CodecApi.LowLatencyMode, true, "low-latency mode");
        CodecApi.TrySet(transform, CodecApi.DefaultBPictureCount, 0u, "B-frames = 0");
        CodecApi.TrySet(transform, CodecApi.GopSize, 15u, "GOP size = 15");

        transform.SetOutputType(0, VideoType(VideoFormatGuids.H264, width, height, fps, bitrate), 0);
        transform.SetInputType(0, VideoType(VideoFormatGuids.NV12, width, height, fps), 0);

        transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        return events;
    }

    /// <summary>Reads SPS/PPS from the output type, polling briefly (Qualcomm's encoder
    /// only publishes the sequence header some milliseconds after init, per ffmpeg).</summary>
    private byte[] ReadSequenceHeader()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return _transform.GetOutputCurrentType(0).GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader);
            }
            catch (SharpGenException ex) when (ex.HResult == HrAttributeNotFound && attempt < 8)
            {
                Thread.Sleep(10);
            }
            catch (SharpGenException ex) when (ex.HResult == HrAttributeNotFound)
            {
                Console.WriteLine("[Host] encoder exposes no sequence header; relying on in-band SPS/PPS.");
                return [];
            }
        }
    }

    /// <summary>Pulls output until the MFT reports it needs more input (sync pumping).</summary>
    private void Drain(List<(byte[] Data, bool Keyframe)> output)
    {
        while (DrainOnce(output))
        {
        }
    }

    /// <summary>One ProcessOutput attempt; returns true when an access unit came out.</summary>
    private bool DrainOnce(List<(byte[] Data, bool Keyframe)> output)
    {
        var buf = new OutputDataBuffer { StreamID = 0 };
        IMFSample? allocated = null;
        IMFMediaBuffer? allocatedBuffer = null;
        if (!_outputProvidesSamples)
        {
            allocated = MediaFactory.MFCreateSample();
            allocatedBuffer = MediaFactory.MFCreateAlignedMemoryBuffer(_outputBufferSize, 15);
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
            // The MFT settled on its real output format after seeing the first frame;
            // accept the renegotiation and refresh everything derived from it.
            allocatedBuffer?.Dispose();
            allocated?.Dispose();
            _transform.SetOutputType(0, _transform.GetOutputAvailableType(0, 0), 0);
            RefreshOutputInfo();
            SequenceHeader = ReadSequenceHeader();
            return false;
        }

        result.CheckError();

        var sample = buf.Sample!;
        var keyframe = false;
        try { keyframe = sample.GetUInt32(SampleAttributeKeys.CleanPoint) != 0; } catch { }

        using var contiguous = sample.ConvertToContiguousBuffer();
        contiguous.Lock(out var ptr, out _, out var length);
        byte[]? data = null;
        if (length > 0)
        {
            data = new byte[length];
            Marshal.Copy(ptr, data, 0, length);
        }
        contiguous.Unlock();
        sample.Dispose();
        // The sample only held a reference; the buffer we created must go too.
        allocatedBuffer?.Dispose();

        // Some MFTs emit empty samples; skip them without counting them as frames.
        if (data is null)
            return true;

        output.Add((data, keyframe));
        _pendingFrames--;
        return true;
    }

    private static IMFMediaType VideoType(Guid subtype, int width, int height, int fps, uint bitrate = 0)
    {
        var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, subtype);
        type.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
        type.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
        type.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
        type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u); // progressive
        if (bitrate > 0)
            type.Set(MediaTypeAttributeKeys.AvgBitrate, bitrate);
        return type;
    }

    private static ulong Pack(int hi, int lo) => ((ulong)hi << 32) | (uint)lo;
}
