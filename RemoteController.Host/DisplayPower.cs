using System.Runtime.InteropServices;
using System.Threading;

namespace RemoteController.Host;

/// <summary>
/// Keeps the machine's display awake while clients are connected. A display that times
/// out mid-session is captured as legitimate black frames (the panel dozes but Desktop
/// Duplication keeps delivering frames), and SendInput-injected control does not reset
/// the display idle timer — so without this the client picture goes black whenever the
/// host machine is left untouched. Sessions are reference-counted; the last disconnect
/// hands the machine's own power policy back.
/// </summary>
public static class DisplayPower
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    private static readonly IntPtr HwndBroadcast = new(0xFFFF);
    private const uint WmSysCommand = 0x0112;
    private static readonly IntPtr ScMonitorPower = new(0xF170);

    private static int _sessions;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public static void KeepAwake()
    {
        if (Interlocked.Increment(ref _sessions) != 1)
            return;

        // Best-effort wake for a panel that dozed off before we got here (execution-state
        // requests only prevent sleep, they do not wake an already-dark panel), then hold
        // the display on for the session's lifetime.
        SendMessage(HwndBroadcast, WmSysCommand, ScMonitorPower, new IntPtr(-1));
        SetThreadExecutionState(EsContinuous | EsDisplayRequired | EsSystemRequired);
        Console.WriteLine("[Host] keeping the display awake while clients are connected");
    }

    public static void Release()
    {
        if (Interlocked.Decrement(ref _sessions) != 0)
            return;

        SetThreadExecutionState(EsContinuous);
        Console.WriteLine("[Host] display power policy restored");
    }
}
