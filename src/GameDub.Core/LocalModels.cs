using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameDub.Core;

public sealed class LocalWorker(string kind) : IDisposable
{
    public string Kind => kind;
    private Process? process;
    private readonly SemaphoreSlim gate = new(1);
    private bool disposed, coldStart;
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
                var python = kind == "tts-f5" ? NaturalVoice.Python : File.Exists(bundled) ? bundled : Path.Combine(Paths.Workspace, ".venv", "Scripts", "python.exe");
                if (!File.Exists(python)) throw new InvalidOperationException("Local runtime missing. Run Setup.ps1 first.");
                var info = new ProcessStartInfo(python)
                {
                    WorkingDirectory = Paths.Workspace, CreateNoWindow = true, UseShellExecute = false,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false)
                };
                info.ArgumentList.Add("-u");
                info.ArgumentList.Add(Path.Combine(Paths.Workspace, "workers", kind == "tts-f5" ? "indicf5_worker.py" : "local_worker.py"));
                info.ArgumentList.Add(kind);
                // No secrets inherited by AI workers. No worker has any network use.
                foreach (var name in info.Environment.Keys.Where(n => Regex.IsMatch(n, "API_KEY|TOKEN|SECRET|PASSWORD", RegexOptions.IgnoreCase)).ToArray()) info.Environment.Remove(name);
                process = Process.Start(info) ?? throw new IOException("Could not start local model worker."); coldStart = true;
                ChildProcessJob.Attach(process);
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (System.ComponentModel.Win32Exception) { }
                process.ErrorDataReceived += (_, _) => { }; // Drain diagnostics; never mix logs with JSON or log private dialogue.
                process.BeginErrorReadLine();
            }
            var running = process;
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(coldStart ? 180 : 60)); coldStart = false; // first request loads the model
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
    /// <summary>Stops the model process to free its memory; the worker stays usable and restarts on the next request.</summary>
    public void Release() { try { if (process is { HasExited: false }) process.Kill(true); } catch (InvalidOperationException) { } }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { if (process is { HasExited: false }) process.Kill(true); } catch (InvalidOperationException) { }
        process?.Dispose();
    }
}

public sealed class OfflineLocalizer(LocalWorker worker,int beamSize = 3) : ILocalizer
{
    private readonly Dictionary<string, string> termTranslations = new();
    public string Identity => (worker.Kind == "translate-nllb" ? $"offline-nllb600m-ct2-int8:beam{beamSize}:noncommercial:no-fixtures-v3"
        : File.Exists(Path.Combine(Paths.Workspace,"models/marian-ct2/model.bin")) ? "offline-marian-en-hi-ct2-int8:beam3:no-fixtures-v3:glossary-v2"
        : "offline-marian-en-hi-onnx-int8:sentence-v1:no-fixtures-v3:glossary-v2")+":long-source-v2:"+ConversationalHindi.Version;
    public async Task<LocalizedLine> TranslateAsync(Dialogue line, GameProfile profile, IReadOnlyList<string> history, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var reviewed = profile.ReviewedLines.FirstOrDefault(p => Text.Key(p.Key) == Text.Key(line.Speaker + ": " + line.English));
        if (string.IsNullOrWhiteSpace(reviewed.Value)) reviewed = profile.ReviewedLines.FirstOrDefault(p => Text.Key(p.Key) == Text.Key(line.English));
        if (!string.IsNullOrWhiteSpace(reviewed.Value)) return new(reviewed.Value, line.Emotion, "game-profile-reviewed-line");
        // The small MT model is sentence-based. It does not understand rolling story context or emotion.
        using var result = await worker.RequestAsync(new { text = line.English,beamSize }, token);
        var hindi = result.RootElement.GetProperty("hindi").GetString()?.Trim() ?? "";
        hindi = ConversationalHindi.Adapt(hindi,profile);
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
    public string Identity => "kokoro82m-v1-hf-promoted-fp32-hindi:clauses140-v4-tail80ms:"+(worker.Kind=="tts-dml"?"dml-depthwise-repair-v1":"cpu2-arena")+":" + mode;
    public IReadOnlyList<LocalizedLine> DeliveryChunks(LocalizedLine line) => SpeechDelivery.Chunks(line);
    public async Task<AudioClip> SynthesizeAsync(LocalizedLine line, string voice, CancellationToken token)
    {
        if (!new[] { "hf_alpha", "hf_beta", "hm_omega", "hm_psi" }.Contains(voice)) voice = "hf_alpha";
        // Kokoro has no documented Hindi emotional acting controls. Only a restrained speed choice.
        var speed = mode == "Performance" ? 1.15 : mode == "Balanced" ? 1.08 : 1.0;
        var sentencePause=mode=="Quality"?.20:.12;
        using var result = await worker.RequestAsync(new { text = line.Hindi, voice, speed,sentencePause }, token);
        return new(Convert.FromBase64String(result.RootElement.GetProperty("pcm").GetString()!),
            "pcm_" + result.RootElement.GetProperty("rate").GetInt32(), Identity);
    }
}

/// <summary>IndicF5 (MIT) GPU voice prompted by licensed synthetic Parler-TTS speakers; Kokoro voice IDs map to the same four profiles.</summary>
public sealed class NaturalVoice(LocalWorker worker, string mode = "Balanced") : IChunkedVoiceProvider
{
    public static string Python => Path.Combine(Paths.Workspace, "runtime-torch", "Scripts", "python.exe");
    public const string TtsName = "IndicF5 Hindi natural (GPU, recommended)";
    public static bool Available => File.Exists(Python) && File.Exists(Path.Combine(Paths.Workspace, "models", "indicf5", "model.safetensors"))
        && new[] { "male_deep", "male_young", "female_expr", "female_calm" }.All(p => File.Exists(Path.Combine(Paths.Workspace, "models", "voice-profiles", p + ".wav")));
    private int Steps => mode == "Quality" ? 16 : 8;
    public string Identity => $"indicf5-nfe{Steps}-parler-refs-v1:" + mode;
    public IReadOnlyList<LocalizedLine> DeliveryChunks(LocalizedLine line) => SpeechDelivery.Chunks(line);
    public async Task<AudioClip> SynthesizeAsync(LocalizedLine line, string voice, CancellationToken token)
    {
        // Never load the GPU voice into a game that has filled video memory (this crashed GTA once).
        // Throwing makes FallbackVoice switch to the CPU Kokoro voice for the rest of the session.
        if (GpuGuard.VoiceShed) throw new InvalidOperationException("Natural voice released to keep the game's video memory free: " + GpuGuard.ShedReason);
        if (worker.ProcessId is null && !GpuGuard.CanLoad(GpuGuard.NaturalVoiceMiB, out var why)) { GpuGuard.Shed(true, false, why); throw new InvalidOperationException("Natural voice not loaded: " + why); }
        using var result = await worker.RequestAsync(new { text = line.Hindi, voice, speed = mode == "Performance" ? 1.08 : 1.0, steps = Steps }, token);
        return new(Convert.FromBase64String(result.RootElement.GetProperty("pcm").GetString()!), "pcm_" + result.RootElement.GetProperty("rate").GetInt32(), Identity);
    }
}

public static class Providers
{
    public const string ConversationalTranslator = "Conversational Hindi (local AI, recommended)";
    public static ILocalizer Localizer(AppSettings s, LocalWorker worker,LocalWorker? nllb = null,LlamaServer? llm = null)
    {
        if(s.Translator.StartsWith("Local sample")) return new FixtureLocalizer();
        var mt = new OfflineLocalizer(s.Translator.StartsWith("Offline NLLB") ? nllb ?? throw new InvalidOperationException("NLLB worker is unavailable") : worker,s.PerformanceMode=="Quality"?3:1);
        // Conversational mode: LLM localisation, with the sentence MT model as the meaning-guard fallback.
        return s.Translator.StartsWith("Conversational") && llm is not null && LlamaServer.Available
            ? new ConversationalLocalizer(llm,new OfflineLocalizer(worker,1),!s.DialoguePolicy.StartsWith("Story")) : mt;
    }
    public static IVoiceProvider Voice(AppSettings s, LocalWorker worker,LocalWorker? gpu = null,Action<string>? fallbackNotice = null,LocalWorker? natural = null)
    {
        if(s.Tts.StartsWith("IndicF5") && natural is not null && NaturalVoice.Available)
            return new CachedVoice(new FallbackVoice(new NaturalVoice(natural,s.PerformanceMode),new KokoroVoice(worker,s.PerformanceMode),fallbackNotice));
        IVoiceProvider voice=s.Tts.StartsWith("eSpeak") ? new EspeakVoice() : new KokoroVoice(worker,s.PerformanceMode);
        if(s.Tts.Contains("GPU") && gpu is not null)voice=new FallbackVoice(new KokoroVoice(gpu,s.PerformanceMode),voice,fallbackNotice);
        return new CachedVoice(voice);
    }
}
