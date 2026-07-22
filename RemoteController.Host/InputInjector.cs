using System.Runtime.InteropServices;
using RemoteController.Shared.Protocol;

namespace RemoteController.Host;

/// <summary>
/// Injects mouse and keyboard input via the Win32 SendInput API. Windows only.
/// </summary>
public static class InputInjector
{
    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const int WheelDelta = 120; // Win32 WHEEL_DELTA, one notch

    public static void MoveMouse(int x, int y, int screenWidth, int screenHeight)
    {
        // MOUSEEVENTF_ABSOLUTE expects coordinates normalized to 0..65535.
        var absX = (int)Math.Round(x * 65535.0 / Math.Max(1, screenWidth - 1));
        var absY = (int)Math.Round(y * 65535.0 / Math.Max(1, screenHeight - 1));
        SendMouse(absX, absY, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, 0);
    }

    public static void MouseButton(RemoteMouseButton button, bool down)
    {
        var flags = (button, down) switch
        {
            (RemoteMouseButton.Left, true) => MOUSEEVENTF_LEFTDOWN,
            (RemoteMouseButton.Left, false) => MOUSEEVENTF_LEFTUP,
            (RemoteMouseButton.Right, true) => MOUSEEVENTF_RIGHTDOWN,
            (RemoteMouseButton.Right, false) => MOUSEEVENTF_RIGHTUP,
            (RemoteMouseButton.Middle, true) => MOUSEEVENTF_MIDDLEDOWN,
            _ => MOUSEEVENTF_MIDDLEUP,
        };
        SendMouse(0, 0, flags, 0);
    }

    /// <param name="steps">Wheel steps; positive = up/away, negative = down/towards.</param>
    public static void MouseWheel(int steps) => SendMouse(0, 0, MOUSEEVENTF_WHEEL, steps * WheelDelta);

    public static void Key(int virtualKey, bool down)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)virtualKey,
                    dwFlags = down ? 0u : KEYEVENTF_KEYUP,
                },
            },
        };
        Send(input);
    }

    private static void SendMouse(int dx, int dy, uint flags, int data)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    mouseData = (uint)data,
                    dwFlags = flags,
                },
            },
        };
        Send(input);
    }

    private static void Send(INPUT input)
    {
        var sent = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        if (sent == 0)
            Console.Error.WriteLine($"[Host] SendInput failed, error {Marshal.GetLastWin32Error()}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
