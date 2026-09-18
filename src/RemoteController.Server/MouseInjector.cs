using System.ComponentModel;
using RemoteController.Protocol;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace RemoteController.Server;

internal static class MouseInjector
{
    private static readonly int VirtualWidth = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN);
    private static readonly int VirtualHeight = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN);

    public static void Inject(MouseMessage message)
    {
        switch (message)
        {
            case MouseMove(var x, var y):
                Send(MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE, x, y);
                break;
            case MouseButton(var button, var isDown):
                var flags = button switch
                {
                    MouseButtonKind.Left => isDown ? MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN : MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP,
                    MouseButtonKind.Right => isDown ? MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN : MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP,
                    MouseButtonKind.Middle => isDown ? MOUSE_EVENT_FLAGS.MOUSEEVENTF_MIDDLEDOWN : MOUSE_EVENT_FLAGS.MOUSEEVENTF_MIDDLEUP,
                    _ => throw new ArgumentOutOfRangeException(nameof(message)),
                };
                // 按键事件不带坐标，沿用系统当前指针位置
                Send(flags, 0, 0);
                break;
            case MouseWheel(var delta):
                // WHEEL_DELTA=120 每格；触控板小数增量按原始精度注入
                Send(MOUSE_EVENT_FLAGS.MOUSEEVENTF_WHEEL, 0, 0, (int)(delta * 120));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(message));
        }
    }

    private static unsafe void Send(MOUSE_EVENT_FLAGS flags, int x, int y, int data = 0)
    {
        if ((uint)x >= VirtualWidth || (uint)y >= VirtualHeight)
            throw new ArgumentOutOfRangeException(nameof(x), "注入坐标超出虚拟屏幕范围");

        // VIRTUALDESK 把 0..65535 映射到整个虚拟屏幕；协议坐标已是虚拟屏幕空间（0 起），直接归一化
        if (flags.HasFlag(MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE))
        {
            flags |= MOUSE_EVENT_FLAGS.MOUSEEVENTF_VIRTUALDESK;
            (x, y) = VirtualCoordinates.Normalize(x, y, VirtualWidth, VirtualHeight);
        }

        var input = new INPUT
        {
            type = INPUT_TYPE.INPUT_MOUSE,
            Anonymous = new INPUT._Anonymous_e__Union
            {
                mi = new MOUSEINPUT
                {
                    dwFlags = flags,
                    dx = x,
                    dy = y,
                    mouseData = (uint)data,
                },
            },
        };

        // SendInput 失败时不保证设置 LastError，只能按返回值判断
        if (PInvoke.SendInput(new[] { input }, sizeof(INPUT)) != 1)
            throw new Win32Exception("SendInput 注入失败");
    }
}
