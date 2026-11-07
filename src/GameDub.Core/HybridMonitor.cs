namespace GameDub.Core;

public sealed class HybridArbiter(IDialogueSink downstream)
{
    private readonly object gate = new();
    private bool subtitlesActive;
    private DateTimeOffset subtitleCleared = DateTimeOffset.MinValue;
    private readonly Queue<(string Key, bool Subtitle, DateTimeOffset At)> history = new();
    public IDialogueSink Subtitles => new Source(this, true);
    public IDialogueSink Audio => new Source(this, false);
    private static string Key(Dialogue line) => new(line.English.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray());
    private void Submit(Dialogue line, bool subtitle)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (subtitle) subtitlesActive = true;
            else if (subtitlesActive || now - subtitleCleared < TimeSpan.FromMilliseconds(1000)) return;
            while (history.TryPeek(out var old) && now - old.At > TimeSpan.FromSeconds(12)) history.Dequeue();
            var key = Key(line);
            // Cross-source deduplication only. A later genuine repeat on the same path is allowed.
            if (history.Any(h => h.Subtitle != subtitle && Similar(h.Key, key))) return;
            history.Enqueue((key, subtitle, now)); while (history.Count > 32) history.Dequeue();
            downstream.Submit(line);
        }
    }
    private static bool Similar(string a, string b)
    {
        if (a == b) return true;
        var x = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var y = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (x.Count == 0 || y.Count == 0) return false;
        return x.Intersect(y).Count() / (double)x.Union(y).Count() >= .7;
    }
    private void Interrupt(bool subtitle)
    {
        lock (gate)
        {
            if (subtitle) { subtitlesActive = false; subtitleCleared = DateTimeOffset.UtcNow; }
            downstream.Interrupt();
        }
    }
    private void SubtitleCleared()
    {
        lock (gate)
        {
            subtitlesActive = false; subtitleCleared = DateTimeOffset.UtcNow;
            downstream.SubtitleCleared();
        }
    }
    private sealed class Source(HybridArbiter owner, bool subtitle) : IDialogueSink
    {
        public void Submit(Dialogue line) => owner.Submit(line, subtitle);
        public void Interrupt() => owner.Interrupt(subtitle);
        public void SubtitleCleared() { if (subtitle) owner.SubtitleCleared(); }
    }
}

public sealed class HybridMonitor(LocalWorker asr)
{
    public event Action<string>? Status;
    public async Task RunAsync(GameWindow game, CaptureRegion region, DialogueRouter router, CancellationToken token)
    {
        var arbiter = new HybridArbiter(router);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var subtitles = new SubtitleMonitor(); subtitles.Status += message => Status?.Invoke(message);
        var audio = new LiveAudioMonitor(asr); audio.Status += message => Status?.Invoke(message);
        var subtitleTask = subtitles.RunAsync(game, region, arbiter.Subtitles, lifetime.Token);
        var audioTask = AudioOrFallbackAsync();
        try { await subtitleTask; }
        finally
        {
            lifetime.Cancel();
            try { await audioTask; } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
        async Task AudioOrFallbackAsync()
        {
            try { await audio.RunAsync(game, arbiter.Audio, lifetime.Token); }
            catch (Exception error) when (error is not OperationCanceledException)
            { Status?.Invoke("Automatic audio fallback unavailable; subtitle dubbing continues. " + error.Message); }
        }
    }
}
