using System;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace RemoteController.Host;

/// <summary>
/// Hand-rolled COM interop for ICodecAPI (codecapi.h), which Vortice.MediaFoundation does
/// not bind. Used to tune encoder/decoder properties before type negotiation (per ffmpeg,
/// some of them only take effect there). The VARIANT is passed as an explicit struct —
/// object-to-VVARIANT marshalling silently corrupts the call and every SetValue comes
/// back E_NOTIMPL.
/// </summary>
internal static class CodecApi
{
    // CODECAPI_AVLowLatencyMode
    public static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    // CODECAPI_AVDecVideoFastDecodeMode
    public static readonly Guid FastDecodeMode = new("6b529f7d-d3b1-49c6-a999-9ec6911bedbf");

    // CODECAPI_AVEncCommonLowLatency (a different, older low-latency switch some MFTs take instead)
    public static readonly Guid CommonLowLatency = new("9d3ecd55-89e8-490a-970a-0c9548d5a56e");

    // CODECAPI_AVEncCommonRateControlMode (eAVEncCommonRateControlMode_LowDelayVBR = 4)
    public static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");

    // CODECAPI_AVEncMPVGOPSize
    public static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");

    // CODECAPI_AVEncMPVDefaultBPictureCount
    public static readonly Guid DefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");

    /// <summary>Sets one codec property, logging the outcome; never throws.</summary>
    public static void TrySet(IMFTransform transform, Guid property, object value, string label)
    {
        try
        {
            if (Marshal.GetObjectForIUnknown(transform.NativePointer) is not ICodecAPI api)
            {
                Console.WriteLine($"[CodecApi] ICodecAPI unavailable; {label} not set.");
                return;
            }

            try
            {
                var hr = api.SetValue(property, value switch
                {
                    // MFTs commonly insist on VT_UI4 even for boolean properties.
                    bool b => ComVariant.FromUInt32(b ? 1u : 0u),
                    uint u => ComVariant.FromUInt32(u),
                    int i => ComVariant.FromUInt32((uint)i),
                    _ => throw new ArgumentException($"unsupported variant type {value.GetType().Name}"),
                });
                Console.WriteLine($"[CodecApi] {label}: {(hr >= 0 ? "ok" : $"rejected (0x{hr:X8})")}");
            }
            finally
            {
                Marshal.ReleaseComObject(api);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CodecApi] {label} failed: {ex.Message}");
        }
    }
}

/// <summary>Minimal 16-byte VARIANT: type + one pointer-sized payload.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ComVariant
{
    public ushort Type;
    public ushort Reserved1, Reserved2, Reserved3;
    public IntPtr Data;

    public static ComVariant FromBool(bool value) =>
        new() { Type = 11 /* VT_BOOL */, Data = value ? -1 : 0 };

    public static ComVariant FromUInt32(uint value) =>
        new() { Type = 19 /* VT_UI4 */, Data = (IntPtr)value };
}

/// <summary>ICodecAPI (strmif.h), declared in full vtable order.</summary>
[ComImport]
[Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecAPI
{
    [PreserveSig]
    int IsSupported(in Guid api);

    [PreserveSig]
    int IsModifiable(in Guid api);

    [PreserveSig]
    int GetParameterRange(in Guid api, out ComVariant valueMin, out ComVariant valueMax, out ComVariant steppingDelta);

    [PreserveSig]
    int GetParameterValues(in Guid api, out IntPtr values, out uint valuesCount);

    [PreserveSig]
    int GetDefaultValue(in Guid api, out ComVariant value);

    [PreserveSig]
    int GetValue(in Guid api, out ComVariant value);

    [PreserveSig]
    int SetValue(in Guid api, in ComVariant value);

    [PreserveSig]
    int RegisterForEvent(in Guid api, IntPtr userData);

    [PreserveSig]
    int UnregisterForEvent(in Guid api);

    [PreserveSig]
    int SetAllDefaults();

    [PreserveSig]
    int SetValueWithNotify(in Guid api, in ComVariant value, out IntPtr changedParam, out uint changedParamCount);

    [PreserveSig]
    int SetAllDefaultsWithNotify(out IntPtr changedParam, out uint changedParamCount);

    [PreserveSig]
    int GetAllSettings(IntPtr stream);

    [PreserveSig]
    int SetAllSettings(IntPtr stream);

    [PreserveSig]
    int SetAllSettingsWithNotify(IntPtr stream, out IntPtr changedParam, out uint changedParamCount);
}
