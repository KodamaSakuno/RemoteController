using System.Net;
using System.Net.Sockets;
using RemoteController.Shared.Protocol;

namespace RemoteController.Host;

public sealed class HostServer(int port)
{
    private const int FrameIntervalMs = 33; // ~30 FPS cap

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Console.WriteLine($"[Host] Listening on 0.0.0.0:{port}");
        Console.WriteLine("[Host] WARNING: no authentication or encryption — use on trusted LANs only.");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = HandleClientAsync(client);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task HandleClientAsync(TcpClient client)
    {
        var endpoint = client.Client.RemoteEndPoint;
        Console.WriteLine($"[Host] Client connected: {endpoint}");

        try
        {
            using (client)
            {
                client.NoDelay = true;
                var stream = new MessageStream(client.GetStream());

                if (await stream.ReadAsync() is not ClientHelloMessage hello || hello.Version != ProtocolInfo.Version)
                    throw new InvalidDataException("Bad handshake from client.");

                using var capture = new DxgiScreenCapture();
                var screenWidth = capture.Width;
                var screenHeight = capture.Height;
                await stream.WriteAsync(new ServerHelloMessage(ProtocolInfo.Version, screenWidth, screenHeight));
                Console.WriteLine($"[Host] Streaming {screenWidth}x{screenHeight} to {endpoint}");

                using var sessionCts = new CancellationTokenSource();
                var sendTask = StreamLoopAsync(stream, capture, sessionCts.Token);
                var receiveTask = ReceiveLoopAsync(stream, screenWidth, screenHeight, sessionCts.Token);

                await Task.WhenAny(sendTask, receiveTask);
                await sessionCts.CancelAsync();
                try
                {
                    await Task.WhenAll(sendTask, receiveTask);
                }
                catch
                {
                    // loop failures are expected during teardown; the session is over either way
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Host] Session error ({endpoint}): {ex.Message}");
        }

        Console.WriteLine($"[Host] Client disconnected: {endpoint}");
    }

    /// <summary>Captures and pushes frames. Runs sequentially so a slow client never queues up stale frames.</summary>
    private static async Task StreamLoopAsync(MessageStream stream, DxgiScreenCapture capture, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var started = Environment.TickCount64;

            // Null means the desktop did not change (AcquireNextFrame already blocked ~100ms),
            // so an idle desktop sends nothing at all.
            if (capture.CaptureJpeg() is { } jpeg)
            {
                await stream.WriteAsync(new FrameMessage(capture.Width, capture.Height, jpeg), cancellationToken);

                var elapsed = (int)(Environment.TickCount64 - started);
                var delay = FrameIntervalMs - elapsed;
                if (delay > 0)
                    await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static async Task ReceiveLoopAsync(MessageStream stream, int screenWidth, int screenHeight, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await stream.ReadAsync(cancellationToken);
            switch (message)
            {
                case null:
                    return; // client closed the connection
                case MouseMoveMessage move:
                    InputInjector.MoveMouse(move.X, move.Y, screenWidth, screenHeight);
                    break;
                case MouseButtonMessage button:
                    InputInjector.MouseButton(button.Button, button.Down);
                    break;
                case MouseWheelMessage wheel:
                    InputInjector.MouseWheel(wheel.Delta);
                    break;
                case KeyEventMessage key:
                    InputInjector.Key(key.VirtualKey, key.Down);
                    break;
            }
        }
    }
}
