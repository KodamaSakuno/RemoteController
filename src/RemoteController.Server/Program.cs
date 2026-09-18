using System.CommandLine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using RemoteController.Protocol;
using RemoteController.Server;
using Windows.Win32;
using Windows.Win32.UI.HiDpi;

// 采集与注入共用物理像素坐标，须在创建任何窗口/GDI 资源前声明 DPI 感知
// PER_MONITOR_AWARE_V2 无生成常量，按 Win32 定义直接取句柄值 (HANDLE)-4
PInvoke.SetProcessDpiAwarenessContext(new DPI_AWARENESS_CONTEXT(new IntPtr(-4)));

// CLI 是参数全集；appsettings 只提供部署默认值，CLI 显式给出时覆盖。--dump-frame 为 CLI 专属诊断项，无配置对应。
var regionOption = new Option<string?>("--region") { Description = "采集区域 x,y,w,h（物理像素，相对虚拟屏幕原点）" };
var urlsOption = new Option<string?>("--urls") { Description = "监听地址，如 http://0.0.0.0:5080" };
var qualityOption = new Option<int?>("--quality") { Description = "JPEG 质量（1-100）" };
var maxFpsOption = new Option<int?>("--max-fps") { Description = "帧率上限" };
var dumpFrameOption = new Option<string?>("--dump-frame") { Description = "抓一帧写入指定 BMP 路径后退出（诊断，不启动服务）" };

var root = new RootCommand("RemoteController.Server —— 被控端：DXGI 采集 + 鼠标注入")
{
    regionOption,
    urlsOption,
    qualityOption,
    maxFpsOption,
    dumpFrameOption,
};

root.SetAction((parseResult, cancellationToken) => RunAsync(
    parseResult.GetValue(regionOption),
    parseResult.GetValue(urlsOption),
    parseResult.GetValue(qualityOption),
    parseResult.GetValue(maxFpsOption),
    parseResult.GetValue(dumpFrameOption),
    cancellationToken));

return await root.Parse(args).InvokeAsync();

static async Task<int> RunAsync(string? cliRegion, string? cliUrls, int? cliQuality, int? cliMaxFps, string? dumpPath, CancellationToken cancellationToken)
{
    var builder = WebApplication.CreateBuilder();
    var config = builder.Configuration;

    // 优先级：CLI > appsettings > 缺省值
    var regionValue = cliRegion ?? config["Region"];
    CaptureRegion? region = null;
    if (regionValue is not null && !CaptureRegion.TryParse(regionValue, out region))
    {
        Console.Error.WriteLine("区域格式应为 x,y,w,h（物理像素，相对虚拟屏幕原点）");
        return 1;
    }

    var urls = cliUrls ?? config["Urls"] ?? "http://0.0.0.0:5080";
    var quality = cliQuality ?? config.GetValue<int?>("Quality") ?? 75;
    var maxFps = cliMaxFps ?? config.GetValue<int?>("MaxFps") ?? 30;

    if (dumpPath is not null)
    {
        // 抓一帧直接落盘，用于 box 联调时查看服务端实际送出的像素，不启动 Web 服务
        using var dumpCapture = new DxgiCapture(region);
        var frame = new byte[dumpCapture.Width * dumpCapture.Height * 4];

        // 屏幕可能静止（AccumulatedFrames 为 0），重试直至拿到一次桌面变化
        var acquired = false;
        for (var attempt = 0; attempt < 10 && !acquired; attempt++)
            acquired = dumpCapture.TryAcquireFrame(frame, 1000);
        if (!acquired)
        {
            Console.Error.WriteLine("未捕获到桌面变化帧");
            return 1;
        }

        FrameDump.WriteBmp(dumpPath, frame, dumpCapture.Width, dumpCapture.Height);
        Console.WriteLine(
            $"Rotation={dumpCapture.Rotation} Buffer={dumpCapture.BufferWidth}x{dumpCapture.BufferHeight} " +
            $"Texture(推导)={dumpCapture.TextureWidth}x{dumpCapture.TextureHeight} " +
            $"Texture(实测)={dumpCapture.LastTextureWidth}x{dumpCapture.LastTextureHeight} " +
            $"Box=({dumpCapture.BoxLeft},{dumpCapture.BoxTop},{dumpCapture.StageWidth},{dumpCapture.StageHeight}) " +
            $"Region={dumpCapture.Region.X},{dumpCapture.Region.Y} {dumpCapture.Region.Width}x{dumpCapture.Region.Height}");
        Console.WriteLine($"已写出 {dumpPath}");
        return 0;
    }

    // appsettings 的 Urls 不自动进入宿主配置，需显式应用
    builder.WebHost.UseUrls(urls);

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

    await app.StartAsync(cancellationToken);
    await ((IHost)app).WaitForShutdownAsync(cancellationToken);
    return 0;
}
