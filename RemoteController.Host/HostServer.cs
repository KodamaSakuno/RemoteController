using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RemoteController.Shared.Protocol;

namespace RemoteController.Host;

public sealed class HostServer(int port, int fps = 30, uint bitrate = 8_000_000)
{
    private readonly int _frameIntervalMs = 1000 / fps;

    // Full-resolution clock for log timestamps (TickCount64 quantizes to ~15.6 ms).
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

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

    private async Task HandleClientAsync(TcpClient client)
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
                Console.WriteLine($"[Host] capture ready (+{Clock.ElapsedMilliseconds} ms)");
                await stream.WriteAsync(new ServerHelloMessage(ProtocolInfo.Version, screenWidth, screenHeight));
                Console.WriteLine($"[Host] Streaming {screenWidth}x{screenHeight} to {endpoint}");

                using var sessionCts = new CancellationTokenSource();
                var sendTask = StreamLoopAsync(stream, capture, screenWidth, screenHeight, sessionCts.Token);
                var receiveTask = ReceiveLoopAsync(stream, screenWidth, screenHeight, sessionCts.Token);

                // A display that times out mid-session streams as black frames; hold it
                // awake for the session (reference-counted across concurrent clients).
                DisplayPower.KeepAwake();
                try
                {
                    await Task.WhenAny(sendTask, receiveTask);
                    await sessionCts.CancelAsync();
                    try
                    {
                        await Task.WhenAll(sendTask, receiveTask);
                    }
                    catch (OperationCanceledException)
                    {
                        // expected during teardown
                    }
                    catch (IOException)
                    {
                        // connection dropped mid-session; the disconnect log line covers it
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Host] Stream loop error ({endpoint}): {ex.Message}");
                    }
                }
                finally
                {
                    DisplayPower.Release();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Host] Session error ({endpoint}): {ex.Message}");
        }

        Console.WriteLine($"[Host] Client disconnected: {endpoint}");
    }

    /// <summary>Captures, encodes and pushes frames. Runs sequentially so a slow client never queues up stale frames.</summary>
    private async Task StreamLoopAsync(MessageStream stream, DxgiScreenCapture capture, int screenWidth, int screenHeight, CancellationToken cancellationToken)
    {
        // Yield before doing anything else: on a quiet desktop every loop iteration can
        // complete synchronously (null frames from the acquire timeout, inline socket
        // writes, no throttle delay), so this method might never hit an incomplete await
        // and therefore never return control to the caller — which would prevent
        // ReceiveLoopAsync from ever being started.
        await Task.Yield();

        var useGpu = true;
        var gpuConversionFailed = false;
        var dozing = false;
        var suppressedBlack = 0L;
        var encoder = NewEncoder(useGpu);

        H264Encoder NewEncoder(bool gpu)
        {
            var created = gpu
                ? new H264Encoder(screenWidth, screenHeight, fps, bitrate, capture.Device)
                : new H264Encoder(screenWidth, screenHeight, fps, bitrate);
            Console.WriteLine($"[Host] encoder ready (+{Clock.ElapsedMilliseconds} ms)");
            return created;
        }

        try
        {
            var headerSent = false;
            long captured = 0, sent = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                var started = Environment.TickCount64;

                // Null means the desktop did not change (AcquireNextFrame already blocked ~100ms),
                // so an idle desktop sends nothing at all. The GPU path hands textures straight
                // to a hardware encoder; the CPU path feeds system memory to the software one.
                List<(byte[] Data, bool Keyframe)>? outputs = null;
                var timestamp = DateTime.UtcNow.Ticks; // 100 ns units, wall clock for client-side lag measurement
                if (useGpu)
                {
                    try
                    {
                        if (encoder.InputIsRgb32)
                        {
                            // RGB32 input: feed BGRA frames as-is, no color conversion at all.
                            if (capture.CaptureBgra() is { } bgraFrame)
                                outputs = encoder.Encode(bgraFrame, timestamp, 333_333);
                        }
                        else if (capture.CaptureFrame() is { } frame)
                        {
                            outputs = encoder.Encode(frame, timestamp, 333_333);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Recreate the encoder without a D3D manager instead of just
                        // flipping a flag: once a manager is set, the MFT will not accept
                        // system-memory input anymore (native crash).
                        Console.WriteLine($"[Host] GPU frame path failed ({ex.Message}); switching to the CPU frame path.");
                        encoder.Dispose();
                        useGpu = false;
                        encoder = NewEncoder(false);
                    }
                }

                if (outputs is null && !useGpu)
                {
                    // GPU-assisted NV12 conversion (render + plane readback) is cheaper than
                    // a full BGRA readback plus per-pixel CPU conversion; the pure-CPU path
                    // stays as the fallback.
                    byte[]? nv12 = null;
                    if (!gpuConversionFailed)
                    {
                        try
                        {
                            nv12 = capture.CaptureNv12Gpu();
                        }
                        catch (Exception ex)
                        {
                            gpuConversionFailed = true;
                            Console.WriteLine($"[Host] GPU NV12 conversion failed ({ex.Message}); using CPU conversion.");
                        }
                    }

                    nv12 ??= capture.CaptureNv12();
                    if (nv12 is not null)
                    {
                        // A dozing display pipeline delivers black frames (luma pinned at
                        // the studio-swing black level); sending them flashes the client's
                        // picture black. The desktop is effectively frozen while dozing, so
                        // suppress the frames instead — the client keeps the last real one.
                        if (IsBlackFrame(nv12, screenWidth * screenHeight))
                        {
                            suppressedBlack++;
                            if (!dozing)
                            {
                                dozing = true;
                                Console.WriteLine("[Host] display pipeline is dozing (black frames); suppressing until it wakes");
                            }

                            // While dozing the compositor still feeds black frames nearly
                            // continuously; without pacing we would capture, convert and
                            // discard in a hot loop. Poll gently instead — the desktop is
                            // effectively frozen anyway.
                            await Task.Delay(100, cancellationToken);
                        }
                        else
                        {
                            if (dozing)
                            {
                                dozing = false;
                                Console.WriteLine($"[Host] display woke; {suppressedBlack} black frames suppressed");
                            }

                            outputs = encoder.Encode(nv12, timestamp, 333_333);
                        }
                    }
                }

                if (outputs is not null)
                {
                    if (captured == 0)
                        Console.WriteLine($"[Host] first frame captured (+{Clock.ElapsedMilliseconds} ms)");
                    captured++;
                    foreach (var (data, keyframe) in outputs)
                    {
                        if (sent == 0)
                            Console.WriteLine($"[Host] first sample encoded (+{Clock.ElapsedMilliseconds} ms)");

                        // A decoder must start on a keyframe preceded by SPS/PPS; the encoder's
                        // first output is always a keyframe, so attach the sequence header to it.
                        if (!headerSent && !keyframe)
                            continue;
                        var payload = data;
                        if (!headerSent)
                        {
                            payload = new byte[encoder.SequenceHeader.Length + data.Length];
                            encoder.SequenceHeader.CopyTo(payload, 0);
                            data.CopyTo(payload, encoder.SequenceHeader.Length);
                            headerSent = true;
                        }

                        await stream.WriteAsync(new VideoFrameMessage(timestamp, keyframe, payload), cancellationToken);
                        if (sent == 0)
                            Console.WriteLine($"[Host] first frame sent (+{Clock.ElapsedMilliseconds} ms)");
                        sent++;
                    }

                    var elapsed = (int)(Environment.TickCount64 - started);
                    var delay = _frameIntervalMs - elapsed;
                    if (delay > 0)
                        await Task.Delay(delay, cancellationToken);
                }
            }
        }
        finally
        {
            encoder.Dispose();
        }
    }

    /// <summary>True when the NV12 frame is essentially all black — the signature of a
    /// dozing display pipeline (luma pinned at the studio-swing black level of 16, plus
    /// encoder noise), not of a real desktop (even a dark one has brighter pixels).
    /// Samples every 7th luma byte; sub-millisecond at 1600p.</summary>
    private static bool IsBlackFrame(byte[] nv12, int yPlaneSize)
    {
        byte max = 0;
        for (var i = 0; i < yPlaneSize; i += 7)
            if (nv12[i] > max)
                max = nv12[i];
        return max < 24;
    }

    private async Task ReceiveLoopAsync(MessageStream stream, int screenWidth, int screenHeight, CancellationToken cancellationToken)
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
