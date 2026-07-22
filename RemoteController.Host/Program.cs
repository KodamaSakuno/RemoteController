using RemoteController.Host;

var port = 47800;
for (var i = 0; i + 1 < args.Length; i++)
{
    if (args[i] == "--port" && int.TryParse(args[i + 1], out var parsed))
        port = parsed;
}

ScreenCapture.EnsureDpiAwareness();

Console.WriteLine("RemoteController.Host — LAN remote-control host (screen + input)");
await new HostServer(port).RunAsync();
