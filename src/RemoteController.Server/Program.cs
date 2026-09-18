using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using RemoteController.Protocol;
using RemoteController.Server;
using Windows.Win32;
using Windows.Win32.UI.HiDpi;

// 采集与注入共用物理像素坐标，须在创建任何窗口/GDI 资源前声明 DPI 感知
// PER_MONITOR_AWARE_V2 无生成常量，按 Win32 定义直接取句柄值 (HANDLE)-4
PInvoke.SetProcessDpiAwarenessContext(new DPI_AWARENESS_CONTEXT(new IntPtr(-4)));

var builder = WebApplication.CreateBuilder(args);

// 区域：命令行 --region 优先，其次配置 Region，缺省整屏
var regionValue = ConfigurationValue(args, "--region") ?? builder.Configuration["Region"];
CaptureRegion? region = null;
if (regionValue is not null && !CaptureRegion.TryParse(regionValue, out region))
{
    Console.Error.WriteLine("区域格式应为 x,y,w,h（物理像素，相对虚拟屏幕原点）");
    return;
}

var quality = builder.Configuration.GetValue("Quality", 75);
var maxFps = builder.Configuration.GetValue("MaxFps", 30);

// --dump-frame <path.bmp>：抓一帧直接落盘，用于 box 联调时查看服务端实际送出的像素，不启动 Web 服务
var dumpPath = ConfigurationValue(args, "--dump-frame");
if (dumpPath is not null)
{
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

    FrameDump.WriteBmp(dumpPath, frame, dumpCapture.Width, dumpCapture.Height);
    Console.WriteLine(
        $"Rotation={dumpCapture.Rotation} Buffer={dumpCapture.BufferWidth}x{dumpCapture.BufferHeight} " +
        $"Texture(推导)={dumpCapture.TextureWidth}x{dumpCapture.TextureHeight} " +
        $"Texture(实测)={dumpCapture.LastTextureWidth}x{dumpCapture.LastTextureHeight} " +
        $"Box=({dumpCapture.BoxLeft},{dumpCapture.BoxTop},{dumpCapture.StageWidth},{dumpCapture.StageHeight}) " +
        $"Region={dumpCapture.Region.X},{dumpCapture.Region.Y} {dumpCapture.Region.Width}x{dumpCapture.Region.Height}");
    Console.WriteLine($"已写出 {dumpPath}");
    return;
}

// 监听地址由配置 Urls 决定（缺省 http://0.0.0.0:5080，见 appsettings.json）；appsettings 不自动进入宿主配置，需显式应用
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://0.0.0.0:5080");

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
    await new RemoteSession(socket, capture, quality, maxFps).RunAsync();
});

app.Run();

/// <summary>取命令行 key 后的一个参数；缺 key 或缺值返回 null。</summary>
static string? ConfigurationValue(string[] args, string key)
{
    var index = Array.IndexOf(args, key);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
