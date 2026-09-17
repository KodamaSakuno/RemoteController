using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using RemoteController.Protocol;
using RemoteController.Server;
using Windows.Win32;
using Windows.Win32.UI.HiDpi;

// 采集与注入共用物理像素坐标，须在创建任何窗口/GDI 资源前声明 DPI 感知
// PER_MONITOR_AWARE_V2 无生成常量，按 Win32 定义直接取句柄值 (HANDLE)-4
PInvoke.SetProcessDpiAwarenessContext(new DPI_AWARENESS_CONTEXT(new IntPtr(-4)));

// --region x,y,w,h 指定采集区域（物理像素，相对虚拟屏幕原点）；缺省为整个虚拟屏幕
CaptureRegion? region = null;
var regionIndex = Array.IndexOf(args, "--region");
if (regionIndex >= 0)
{
    if (regionIndex + 1 >= args.Length || !CaptureRegion.TryParse(args[regionIndex + 1], out var parsed))
    {
        Console.Error.WriteLine("用法: --region x,y,w,h（物理像素，相对虚拟屏幕原点）");
        return;
    }
    region = parsed;
}

// --dump-frame <path.bmp>：抓一帧直接落盘，用于 box 联调时查看服务端实际送出的像素，不启动 Web 服务
var dumpIndex = Array.IndexOf(args, "--dump-frame");
if (dumpIndex >= 0)
{
    if (dumpIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("用法: --dump-frame <path.bmp>");
        return;
    }

    var path = args[dumpIndex + 1];
    using var dumpCapture = new DxgiCapture(region);
    var frame = new byte[dumpCapture.Width * dumpCapture.Height * 4];

    // 屏幕可能静止（AccumulatedFrames 为 0），重试直至拿到一次桌面变化
    var acquired = false;
    for (var attempt = 0; attempt < 10 && !acquired; attempt++)
        acquired = dumpCapture.TryAcquireFrame(frame, 1000);
    if (!acquired)
    {
        Console.Error.WriteLine("未捕获到桌面变化帧");
        return;
    }

    FrameDump.WriteBmp(path, frame, dumpCapture.Width, dumpCapture.Height);
    Console.WriteLine(
        $"Rotation={dumpCapture.Rotation} Buffer={dumpCapture.BufferWidth}x{dumpCapture.BufferHeight} " +
        $"Texture(推导)={dumpCapture.TextureWidth}x{dumpCapture.TextureHeight} " +
        $"Texture(实测)={dumpCapture.LastTextureWidth}x{dumpCapture.LastTextureHeight} " +
        $"Box=({dumpCapture.BoxLeft},{dumpCapture.BoxTop},{dumpCapture.StageWidth},{dumpCapture.StageHeight}) " +
        $"Region={dumpCapture.Region.X},{dumpCapture.Region.Y} {dumpCapture.Region.Width}x{dumpCapture.Region.Height}");
    Console.WriteLine($"已写出 {path}");
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5080");

var app = builder.Build();
app.UseWebSockets();

// 单一采集实例供当前唯一客户端复用，避免每连接重复建 D3D 资源
using var capture = new DxgiCapture(region);

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
