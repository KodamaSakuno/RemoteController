using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using RemoteController.Server;
using Windows.Win32;
using Windows.Win32.UI.HiDpi;

// 采集与注入共用物理像素坐标，须在创建任何窗口/GDI 资源前声明 DPI 感知
// PER_MONITOR_AWARE_V2 无生成常量，按 Win32 定义直接取句柄值 (HANDLE)-4
PInvoke.SetProcessDpiAwarenessContext(new DPI_AWARENESS_CONTEXT(new IntPtr(-4)));

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5080");

var app = builder.Build();
app.UseWebSockets();

// 单一采集实例供当前唯一客户端复用，避免每连接重复创建 DIB section
using var capture = new ScreenCapture();

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await new RemoteSession(socket, capture).RunAsync();
});

app.Run();
