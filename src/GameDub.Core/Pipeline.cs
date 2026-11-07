using System.Diagnostics;
using System.Threading.Channels;

namespace GameDub.Core;

public sealed class DubbingPipeline(ILocalizer localizer, IVoiceProvider voice, IAudioPlayer player, DialogueCache cache, GameProfile profile, int maxAgeMs = 6000)
{
    public DialogueContextMemory Context { get; } = new();
    public LatencyMetrics Metrics { get; } = new();
    public event Action<Dialogue, LocalizedLine, AudioClip>? Ready;
    public event Action<Dialogue, LocalizedLine>? Localized;
    public event Action<Dialogue, LocalizedLine>? Playing;
    public event Action<string>? Status;

    public async Task<LatencyRecord> ProcessAsync(Dialogue line, CancellationToken token, IAudioPlayer? playback = null)
    {
        var destination = playback ?? player;
        var watch = Stopwatch.StartNew();
        var history = Context.Snapshot();
        var voiceId = profile.VoiceFor(line.Speaker);
        var key = DialogueCache.Identity(line, profile, localizer.Identity, voice.Identity, voiceId, history);
        var item = cache.Get(key);
        var hit = item is not null;
        double translationMs = 0, synthesisMs = 0;
        LocalizedLine translated;
        AudioClip clip;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            deadline.CancelAfter(maxAgeMs);
            try
            {
                if (item is { } cached) { translated = cached.Line; clip = cached.Clip; }
                else
                {
                    Status?.Invoke("Localizing dialogue…");
                    translated = await localizer.TranslateAsync(line, profile, history, deadline.Token);
                    translationMs = watch.Elapsed.TotalMilliseconds;
                    Localized?.Invoke(line, translated);
                    if (voice is IChunkedVoiceProvider chunked && chunked.DeliveryChunks(translated) is { Count: > 1 } chunks)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        return await PlayChunksAsync(line, translated, chunks, voiceId, key, translationMs, watch, destination, token);
                    }
                    Status?.Invoke("Generating Hindi voice…");
                    var start = watch.Elapsed.TotalMilliseconds;
                    clip = await voice.SynthesizeAsync(translated, voiceId, deadline.Token);
                    synthesisMs = watch.Elapsed.TotalMilliseconds - start;
                    deadline.Token.ThrowIfCancellationRequested();
                    cache.Put(key, translated, clip);
                }
                deadline.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new TimeoutException($"Dropped stale dialogue: exceeded {maxAgeMs} ms generation budget. No delayed playback."); }
        }
        token.ThrowIfCancellationRequested();
        if (watch.ElapsedMilliseconds > maxAgeMs) throw new TimeoutException("Dropped stale dialogue before playback.");
        Ready?.Invoke(line, translated, clip);
        double firstAudioMs = 0;
        LatencyRecord? measured = null;
        await destination.PlayAsync(clip, () =>
        {
            Playing?.Invoke(line, translated);
            firstAudioMs = watch.Elapsed.TotalMilliseconds;
            Context.Add(line, translated);
            measured = new(DateTimeOffset.Now, localizer.Identity + "/" + voice.Identity, hit, translationMs, synthesisMs, firstAudioMs, 0);
            Metrics.Add(measured);
            Status?.Invoke($"Hindi playing • {(hit ? "cache" : "generated")} • playback requested at {firstAudioMs:0} ms");
        }, token);
        return (measured ?? new LatencyRecord(DateTimeOffset.Now, voice.Identity, hit, translationMs, synthesisMs, firstAudioMs, 0)) with { TotalMs = watch.Elapsed.TotalMilliseconds };
    }

    private async Task<LatencyRecord> PlayChunksAsync(Dialogue original, LocalizedLine translated, IReadOnlyList<LocalizedLine> chunks,
        string voiceId, string key, double translationMs, Stopwatch watch, IAudioPlayer destination, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var queue = Channel.CreateBounded<AudioClip>(new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var clips = new List<AudioClip>();
        double synthesisMs = 0, firstMs = 0;
        var started = false;
        var producer = ProduceAsync();
        try
        {
            await foreach (var clip in queue.Reader.ReadAllAsync(lifetime.Token))
            {
                lifetime.Token.ThrowIfCancellationRequested();
                if (!started && watch.ElapsedMilliseconds > maxAgeMs) throw new TimeoutException("Dropped stale dialogue before first sentence playback.");
                await destination.PlayAsync(clip, () =>
                {
                    if (started) return;
                    Playing?.Invoke(original, translated);
                    started = true; firstMs = watch.Elapsed.TotalMilliseconds; Context.Add(original, translated);
                    Metrics.Add(new(DateTimeOffset.Now, localizer.Identity + "/" + voice.Identity, false, translationMs, synthesisMs, firstMs, 0));
                    Status?.Invoke($"Hindi playing • sentence chunks • playback requested at {firstMs:0} ms");
                }, lifetime.Token);
            }
            await producer;
            lifetime.Token.ThrowIfCancellationRequested();
            if (clips.Count == 0) throw new InvalidDataException("Voice provider produced no delivery chunks.");
            var format = clips[0].Format;
            if (clips.Any(c => c.Format != format)) throw new InvalidDataException("Voice provider changed audio format between sentences.");
            using var combined = new MemoryStream();
            foreach (var clip in clips) combined.Write(clip.Bytes);
            var complete = new AudioClip(combined.ToArray(), format, voice.Identity);
            cache.Put(key, translated, complete);
            Ready?.Invoke(original, translated, complete);
            return new(DateTimeOffset.Now, localizer.Identity + "/" + voice.Identity, false, translationMs, synthesisMs, firstMs, watch.Elapsed.TotalMilliseconds);
        }
        finally
        {
            lifetime.Cancel();
            try { await producer; } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
        async Task ProduceAsync()
        {
            try
            {
                foreach (var chunk in chunks)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    var remaining = started ? maxAgeMs : maxAgeMs - (int)watch.ElapsedMilliseconds;
                    if (remaining <= 0) throw new TimeoutException("Generation budget expired before first sentence.");
                    deadline.CancelAfter(remaining);
                    var timing = Stopwatch.StartNew();
                    AudioClip clip;
                    try { clip = await voice.SynthesizeAsync(chunk, voiceId, deadline.Token); }
                    catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { throw new TimeoutException("A Hindi sentence exceeded its generation budget."); }
                    synthesisMs += timing.Elapsed.TotalMilliseconds;
                    lifetime.Token.ThrowIfCancellationRequested();
                    clips.Add(clip);
                    await queue.Writer.WriteAsync(clip, lifetime.Token);
                }
                queue.Writer.TryComplete();
            }
            catch (Exception error) { queue.Writer.TryComplete(error); throw; }
        }
    }
}

// Inputs distinguish a short subtitle gap from a hard Stop/focus loss.
public interface IDialogueSink
{
    void Submit(Dialogue line);
    void Interrupt();
    // A subtitle disappearing is an input gap, not a replacement dialogue or Stop.
    // Keep the last confirmed line within its generation deadline.
    void SubtitleCleared() { }
}
public sealed class DialogueRouter : IAsyncDisposable, IDialogueSink
{
    private sealed record QueuedLine(Dialogue Line, long CapturedAt);
    private readonly Channel<QueuedLine> queue;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private readonly HashSet<CancellationTokenSource> active = new();
    private readonly SemaphoreSlim slots;
    private readonly bool preserveLines;
    private readonly int maxDelayMs;
    private readonly DubbingPipeline pipeline;
    private readonly IAudioPlayer player;
    private readonly Task worker;
    private Task tail = Task.CompletedTask;
    private long dropped;
    public long DroppedCount => Interlocked.Read(ref dropped);
    public int PendingCount { get { lock(gate) return queue.Reader.Count; } }
    public event Action<string>? Error;
    public event Action<string>? Status;
    public DialogueRouter(DubbingPipeline pipeline, IAudioPlayer player, bool preserveLines = false, int maxDelayMs = 10000)
    {
        this.pipeline = pipeline; this.player = player;
        this.preserveLines = preserveLines; this.maxDelayMs = Math.Clamp(maxDelayMs, 100, 30000);
        queue = Channel.CreateBounded<QueuedLine>(new BoundedChannelOptions(preserveLines ? 2 : 1)
        { SingleReader = false, FullMode = BoundedChannelFullMode.Wait });
        slots = new(preserveLines ? 2 : 1); // current dialogue + one prepared ahead; never concurrent playback
        worker = Task.Run(ConsumeAsync);
    }
    public void Submit(Dialogue line)
    {
        var overflow = false;
        lock (gate)
        {
            if (lifetime.IsCancellationRequested) return;
            if (!preserveLines)
            {
                foreach(var run in active) run.Cancel();
                player.Stop();
            }
            var entry = new QueuedLine(line, Stopwatch.GetTimestamp());
            if (!queue.Writer.TryWrite(entry))
            {
                if(queue.Reader.TryRead(out _)) Interlocked.Increment(ref dropped);
                queue.Writer.TryWrite(entry);
                overflow = preserveLines;
            }
        }
        if(overflow) Status?.Invoke($"Dialogue buffer full • {DroppedCount} waiting line(s) skipped to limit delay");
    }
    public void Interrupt()
    {
        lock (gate) { while (queue.Reader.TryRead(out _)) { } foreach(var run in active) run.Cancel(); player.Stop(); }
    }
    private async Task ConsumeAsync()
    {
        try
        {
            while (await queue.Reader.WaitToReadAsync(lifetime.Token))
            {
                await slots.WaitAsync(lifetime.Token);
                QueuedLine? entry;
                CancellationTokenSource run;
                lock (gate)
                {
                    if (!queue.Reader.TryRead(out entry)) { slots.Release(); continue; }
                    run = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    active.Add(run);
                }
                var previous = tail;
                var processing = RunLineAsync(entry, run, previous);
                // A failed prefetch must not allow a later line to overtake earlier playback.
                tail = Task.WhenAll(previous, processing);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    private async Task RunLineAsync(QueuedLine entry, CancellationTokenSource run, Task previous)
    {
        try
        {
            if (preserveLines && Stopwatch.GetElapsedTime(entry.CapturedAt).TotalMilliseconds > maxDelayMs)
            { Interlocked.Increment(ref dropped); Status?.Invoke($"Skipped expired waiting dialogue • {DroppedCount} dropped"); return; }
            var output = preserveLines ? new OrderedPlayer(player, previous, entry.CapturedAt, maxDelayMs) : player;
            await pipeline.ProcessAsync(entry.Line, run.Token, output);
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested) { }
        catch (Exception e) { Interlocked.Increment(ref dropped); Error?.Invoke(e.Message); }
        finally { lock(gate) { active.Remove(run); run.Dispose(); } slots.Release(); }
    }
    private sealed class OrderedPlayer(IAudioPlayer inner, Task previous, long capturedAt, int maxDelayMs) : IAudioPlayer
    {
        private bool started;
        public async Task PlayAsync(AudioClip clip, Action onStarted, CancellationToken token)
        {
            await previous.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if(!started && Stopwatch.GetElapsedTime(capturedAt).TotalMilliseconds > maxDelayMs)
                throw new TimeoutException("Dialogue expired while waiting for playback; skipped to prevent a growing delay.");
            await inner.PlayAsync(clip, () => { started = true; onStarted(); }, token);
        }
        public void Stop() => inner.Stop();
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); Interrupt(); queue.Writer.TryComplete();
        await worker; await tail; lifetime.Dispose(); slots.Dispose();
    }
}

public enum SubtitleEvent { None, Line, Cleared }
public record SubtitleObservation(SubtitleEvent Event, string Text = "");
public sealed class SubtitleChangeDetector
{
    private string candidate = "", emitted = "";
    private TimeSpan since, blankSince;
    private bool blank;
    private int repeats;
    private static string OcrKey(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    public SubtitleObservation Observe(string raw, TimeSpan now)
    {
        var text = Text.Clean(raw);
        if (new[] { "paused", "loading", "loading...", "saving...", "game paused", "press any key" }.Contains(text.ToLowerInvariant())) text = "";
        if (text.Length < 3 || text.Count(char.IsLetter) < 2)
        {
            candidate = ""; repeats = 0;
            if (!blank) { blank = true; blankSince = now; }
            if (now - blankSince >= TimeSpan.FromMilliseconds(700) && emitted.Length > 0)
            { emitted = ""; return new(SubtitleEvent.Cleared); }
            return new(SubtitleEvent.None);
        }
        blank = false;
        if (OcrKey(text) != OcrKey(candidate)) { candidate = text; since = now; repeats = 1; return new(SubtitleEvent.None); }
        repeats++;
        var settled = text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?') || text.EndsWith('"');
        var stableMs = settled ? 140 : 280;
        if (repeats >= 2 && (now - since).TotalMilliseconds >= stableMs && OcrKey(emitted) != OcrKey(text))
        { emitted = text; return new(SubtitleEvent.Line, text); }
        return new(SubtitleEvent.None);
    }
}
