using System.Diagnostics;
using System.Threading.Channels;

namespace GameDub.Core;

public sealed class LiveAudioMonitor(LocalWorker recognizer)
{
    public event Action<string>? Status;
    public async Task RunAsync(GameWindow game, IDialogueSink router, CancellationToken token)
    {
        await recognizer.WarmAsync(token);
        var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var capture = new ProcessAudioCapture();
        capture.DataAvailable += bytes => queue.Writer.TryWrite(bytes);
        using var running = CancellationTokenSource.CreateLinkedTokenSource(token);
        var captureTask = Task.Run(async () =>
        {
            try { await capture.RunAsync(game.ProcessId, running.Token); queue.Writer.TryComplete(); }
            catch (Exception e) { queue.Writer.TryComplete(e); throw; }
        }, running.Token);
        var watchdog = Task.Run(async () =>
        {
            var wasForeground = true;
            while (!running.IsCancellationRequested)
            {
                if (!GameWindows.IsAlive(game))
                {
                    var replacement = GameWindows.Rebind(game);
                    if (replacement is null) { router.Interrupt(); Status?.Invoke($"Selected window closed: {game.Title}. Select the running game."); running.Cancel(); break; }
                    game = replacement;
                }
                var nowForeground = GameWindows.GetForegroundWindow() == game.Handle;
                if (wasForeground && !nowForeground) router.Interrupt();
                wasForeground = nowForeground;
                await Task.Delay(250, running.Token);
            }
        }, running.Token);
        var previous = "";
        var foreground = true;
        var lastLine = Stopwatch.StartNew();
        try
        {
            Status?.Invoke("Local speech recognition active • selected process only • no microphone");
            await foreach (var pcm in queue.Reader.ReadAllAsync(running.Token))
            {
                if (!GameWindows.IsAlive(game)) continue; // The watchdog rebinds or cancels the selected process.
                if (GameWindows.GetForegroundWindow() != game.Handle)
                {
                    if (foreground) { router.Interrupt(); using var reset = await recognizer.RequestAsync(new { op = "reset" }, running.Token); foreground = false; }
                    continue;
                }
                foreground = true;
                using var response = await recognizer.RequestAsync(new { pcm = Convert.ToBase64String(pcm) }, running.Token);
                var text = response.RootElement.GetProperty("text").GetString() ?? "";
                if (text.Length < 3) continue;
                var confidence = response.RootElement.TryGetProperty("confidence", out var c) ? c.GetDouble() : 0;
                if (confidence < .60) { Status?.Invoke($"Uncertain game speech dropped ({confidence:P0}). Music/effects can confuse local ASR."); continue; }
                if (Text.Key(text) == previous && lastLine.ElapsedMilliseconds < 2500) continue;
                previous = Text.Key(text); lastLine.Restart();
                router.Submit(new(text));
                Status?.Invoke($"Game speech recognized ({confidence:P0}) • identity unknown");
            }
            await captureTask;
        }
        finally
        {
            running.Cancel();
            try { await watchdog; } catch (OperationCanceledException) when (running.IsCancellationRequested) { }
            try { await captureTask; } catch (OperationCanceledException) when (running.IsCancellationRequested) { }
        }
    }
}
