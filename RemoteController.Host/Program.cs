using RemoteController.Host;

var port = 47800;
var fps = 30;
var bitrateMbps = 8;
for (var i = 0; i + 1 < args.Length; i++)
{
    if (args[i] == "--port" && int.TryParse(args[i + 1], out var parsedPort))
        port = parsedPort;
    if (args[i] == "--fps" && int.TryParse(args[i + 1], out var parsedFps))
        fps = parsedFps;
    if (args[i] == "--bitrate" && int.TryParse(args[i + 1], out var parsedBitrate))
        bitrateMbps = parsedBitrate;
}

DxgiScreenCapture.EnsureDpiAwareness();

Console.WriteLine("RemoteController.Host — LAN remote-control host (screen + input)");
await new HostServer(port, fps, (uint)bitrateMbps * 1_000_000).RunAsync();
