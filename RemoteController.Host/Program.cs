using RemoteController.Host;

DxgiScreenCapture.EnsureDpiAwareness();

var port = 47800;
var fps = 30;
var bitrateMbps = 8;
var rawPort = 0;
for (var i = 0; i + 1 < args.Length; i++)
{
    if (args[i] == "--port" && int.TryParse(args[i + 1], out var parsedPort))
        port = parsedPort;
    if (args[i] == "--fps" && int.TryParse(args[i + 1], out var parsedFps))
        fps = parsedFps;
    if (args[i] == "--bitrate" && int.TryParse(args[i + 1], out var parsedBitrate))
        bitrateMbps = parsedBitrate;
    if (args[i] == "--raw-port" && int.TryParse(args[i + 1], out var parsedRawPort))
        rawPort = parsedRawPort;
}

if (rawPort == port)
{
    Console.WriteLine("[Host] --raw-port must differ from --port; raw stream disabled.");
    rawPort = 0;
}

Console.WriteLine("RemoteController.Host — LAN remote-control host (screen + input)");
await new HostServer(port, fps, (uint)bitrateMbps * 1_000_000, rawPort).RunAsync();
