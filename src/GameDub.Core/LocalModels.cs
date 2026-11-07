using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameDub.Core;

public sealed class LocalWorker(string kind) : IDisposable
{
    private Process? process;
    private readonly SemaphoreSlim gate = new(1);
    private bool disposed;
    public int? ProcessId => process is { HasExited: false } ? process.Id : null;
    public long WorkingSetBytes { get { try { process?.Refresh(); return process is { HasExited: false } ? process.WorkingSet64 : 0; } catch (InvalidOperationException) { return 0; } } }
    public async Task<JsonDocument> RequestAsync(object payload, CancellationToken token)
    {
        await gate.WaitAsync(token);
        // Once sent, finish/drain the bounded inference to keep the warm process and
        // stdout protocol aligned. Cancellation returns immediately to the router;
        // the discarded result can never reach playback. No second job runs concurrently.
        var pending = ExchangeAsync(payload);
        try { return await pending.WaitAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { _ = DiscardAsync(pending); throw; }
    }
    private static async Task DiscardAsync(Task<JsonDocument> pending)
    {
        try { using var ignored = await pending; } catch (Exception) { /* observed; next request can restart a failed worker */ }
    }
    private async Task<JsonDocument> ExchangeAsync(object payload)
    {
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (process is null || process.HasExited)
            {
                var bundled = Path.Combine(Paths.Workspace, "runtime", "python.exe");
                var python = File.Exists(bundled) ? bundled : Path.Combine(Paths.Workspace, ".venv", "Scripts", "python.exe");
                if (!File.Exists(python)) throw new InvalidOperationException("Local runtime missing. Run Setup.ps1 first.");
                var info = new ProcessStartInfo(python)
                {
                    WorkingDirectory = Paths.Workspace, CreateNoWindow = true, UseShellExecute = false,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false)
                };
                info.ArgumentList.Add("-u");
                info.ArgumentList.Add(Path.Combine(Paths.Workspace, "workers", "local_worker.py"));
                info.ArgumentList.Add(kind);
                // No secrets inherited by AI workers. No worker has any network use.
                foreach (var name in info.Environment.Keys.Where(n => Regex.IsMatch(n, "API_KEY|TOKEN|SECRET|PASSWORD", RegexOptions.IgnoreCase)).ToArray()) info.Environment.Remove(name);
                process = Process.Start(info) ?? throw new IOException("Could not start local model worker.");
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (System.ComponentModel.Win32Exception) { }
                process.ErrorDataReceived += (_, _) => { }; // Drain diagnostics; never mix logs with JSON or log private dialogue.
                process.BeginErrorReadLine();
            }
            var running = process;
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var abort = watchdog.Token.Register(() => { try { if (!running.HasExited) running.Kill(true); } catch (InvalidOperationException) { } });
            await running.StandardInput.WriteLineAsync(JsonSerializer.Serialize(payload).AsMemory(), watchdog.Token);
            await running.StandardInput.FlushAsync(watchdog.Token);
            var response = await running.StandardOutput.ReadLineAsync(watchdog.Token);
            if (response is null) throw new IOException("Local model worker stopped. Retry; no obsolete audio was played.");
            var result = JsonDocument.Parse(response);
            if (!result.RootElement.GetProperty("ok").GetBoolean())
            {
                var message = result.RootElement.GetProperty("error").GetString(); result.Dispose();
                throw new InvalidOperationException(message);
            }
            return result;
        }
        finally { gate.Release(); }
    }
    public async Task WarmAsync(CancellationToken token) { using var result = await RequestAsync(new { op = "warmup" }, token); }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { if (process is { HasExited: false }) process.Kill(true); } catch (InvalidOperationException) { }
        process?.Dispose();
    }
}

public sealed class OfflineLocalizer(LocalWorker worker) : ILocalizer
{
    private readonly List<DialogueSample> curated = Json.Samples();
    private readonly Dictionary<string, string> termTranslations = new();
    public string Identity => "offline-marian-en-hi-onnx-int8:sentence-v1:curated-v2:glossary-v2";
    public async Task<LocalizedLine> TranslateAsync(Dialogue line, GameProfile profile, IReadOnlyList<string> history, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var reviewed = profile.ReviewedLines.FirstOrDefault(p => Text.Key(p.Key) == Text.Key(line.Speaker + ": " + line.English));
        if (string.IsNullOrWhiteSpace(reviewed.Value)) reviewed = profile.ReviewedLines.FirstOrDefault(p => Text.Key(p.Key) == Text.Key(line.English));
        if (!string.IsNullOrWhiteSpace(reviewed.Value)) return new(reviewed.Value, line.Emotion, "game-profile-reviewed-line");
        // Curated reference examples are explicit overrides, never passed off as MT model evaluations.
        var exact = curated.Where(s => Text.Key(s.English) == Text.Key(line.English)).ToArray();
        var sample = exact.FirstOrDefault(s => s.Context == line.Context) ?? (exact.Length == 1 ? exact[0] : null);
        if (sample is not null) return new(await ApplyGlossaryAsync(line.English, sample.Hindi, profile, token), line.Emotion == "neutral" ? sample.Emotion : line.Emotion, "curated-dialogue-override");
        // The small MT model is sentence-based. It does not understand rolling story context or emotion.
        using var result = await worker.RequestAsync(new { text = line.English }, token);
        var hindi = result.RootElement.GetProperty("hindi").GetString()?.Trim() ?? "";
        hindi = await ApplyGlossaryAsync(line.English, hindi, profile, token);
        if (string.IsNullOrWhiteSpace(hindi) || !hindi.Any(c => c is >= '\u0900' and <= '\u097F'))
            throw new InvalidDataException("Offline translator did not produce Hindi. Line dropped.");
        return new(hindi, line.Emotion, Identity);
    }
    private async Task<string> ApplyGlossaryAsync(string english, string hindi, GameProfile profile, CancellationToken token)
    {
        foreach (var term in profile.Glossary.Where(p => !string.IsNullOrWhiteSpace(p.Key) && !string.IsNullOrWhiteSpace(p.Value)).OrderByDescending(p => p.Key.Length))
        {
            if (!Regex.IsMatch(english, @"(?<!\w)" + Regex.Escape(term.Key) + @"(?!\w)", RegexOptions.IgnoreCase)) continue;
            if (hindi.Contains(term.Value, StringComparison.OrdinalIgnoreCase)) continue;
            var original = term.Key;
            if (!hindi.Contains(original, StringComparison.OrdinalIgnoreCase))
            {
                if (!termTranslations.TryGetValue(term.Key, out original))
                {
                    using var translated = await worker.RequestAsync(new { text = term.Key }, token);
                    original = translated.RootElement.GetProperty("hindi").GetString()?.Trim().TrimEnd('.', '!', '?', '।') ?? "";
                    if (termTranslations.Count < 128) termTranslations[term.Key] = original;
                }
            }
            if (string.IsNullOrWhiteSpace(original) || !hindi.Contains(original, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A glossary term could not be verified in this translation. Add the complete line to ReviewedLines in the profile; uncertain dialogue was dropped.");
            hindi = Regex.Replace(hindi, Regex.Escape(original), _ => term.Value, RegexOptions.IgnoreCase);
        }
        return hindi;
    }
}

public sealed class KokoroVoice(LocalWorker worker, string mode = "Balanced") : IChunkedVoiceProvider
{
    public string Identity => "kokoro82m-v1-hf-promoted-fp32-hindi:sentences-v2:cpu2:" + mode;
    public IReadOnlyList<LocalizedLine> DeliveryChunks(LocalizedLine line) => Regex.Split(line.Hindi.Trim(), @"(?<=[।.!?])\s+")
        .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => line with { Hindi = s }).ToArray();
    public async Task<AudioClip> SynthesizeAsync(LocalizedLine line, string voice, CancellationToken token)
    {
        if (!new[] { "hf_alpha", "hf_beta", "hm_omega", "hm_psi" }.Contains(voice)) voice = "hf_alpha";
        // Kokoro has no documented Hindi emotional acting controls. Only a restrained speed choice.
        var speed = mode == "Performance" ? 1.05 : 1.0;
        using var result = await worker.RequestAsync(new { text = line.Hindi, voice, speed }, token);
        return new(Convert.FromBase64String(result.RootElement.GetProperty("pcm").GetString()!),
            "pcm_" + result.RootElement.GetProperty("rate").GetInt32(), Identity);
    }
}

public static class Providers
{
    public static ILocalizer Localizer(AppSettings s, LocalWorker worker) => s.Translator.StartsWith("Local sample") ? new FixtureLocalizer() : new OfflineLocalizer(worker);
    public static IVoiceProvider Voice(AppSettings s, LocalWorker worker) => s.Tts.StartsWith("eSpeak") ? new EspeakVoice() : new KokoroVoice(worker, s.PerformanceMode);
}
