using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameDub.Core;

/// <summary>
/// One persistent llama.cpp server (Vulkan GPU, loopback only, random port, no web UI) hosting a small
/// instruction model. Started lazily, reused for every line, killed with the app.
/// </summary>
public sealed class LlamaServer : IDisposable
{
    public static string Executable => Path.Combine(Paths.Workspace, "runtime-llama", "bin", "llama-server.exe");
    public static string ModelPath => Directory.Exists(Path.Combine(Paths.Workspace, "models", "llm"))
        ? Directory.GetFiles(Path.Combine(Paths.Workspace, "models", "llm"), "*.gguf").Where(f => !Path.GetFileName(f).StartsWith("mmproj") && !Path.GetFileName(f).StartsWith("mtp-")).OrderBy(f => f).FirstOrDefault() ?? ""
        : "";
    public static bool Available => File.Exists(Executable) && File.Exists(ModelPath);
    public string ModelName => Path.GetFileNameWithoutExtension(ModelPath);
    private readonly SemaphoreSlim start = new(1), gate = new(1);
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private Process? process; private int port; private bool disposed;
    public int? ProcessId => process is { HasExited: false } ? process.Id : null;
    public long WorkingSetBytes { get { try { process?.Refresh(); return process is { HasExited: false } ? process.WorkingSet64 : 0; } catch (InvalidOperationException) { return 0; } } }

    private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    public async Task EnsureStartedAsync(CancellationToken token)
    {
        if (process is { HasExited: false }) return;
        await start.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (process is { HasExited: false }) return;
            if (!Available) throw new InvalidOperationException("Conversational Hindi model is not installed (runtime-llama / models/llm).");
            if (GpuGuard.LlmShed) throw new InvalidOperationException("Conversational model released to keep the game's video memory free: " + GpuGuard.ShedReason);
            if (!GpuGuard.CanLoad(GpuGuard.LlmMiB, out var why)) { GpuGuard.Shed(false, true, why); throw new InvalidOperationException("Conversational model not loaded: " + why); }
            port = FreePort();
            var info = new ProcessStartInfo(Executable) { WorkingDirectory = Path.GetDirectoryName(Executable)!, CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "-m", ModelPath, "--host", "127.0.0.1", "--port", port.ToString(), "-ngl", "99", "-c", "2048", "--parallel", "1", "-ot", "per_layer=CPU",
                "-t", "2", "--no-webui", "-fa", "on", "--reasoning-format", "deepseek" }) info.ArgumentList.Add(arg);
            foreach (var name in info.Environment.Keys.Where(n => Regex.IsMatch(n, "API_KEY|TOKEN|SECRET|PASSWORD", RegexOptions.IgnoreCase)).ToArray()) info.Environment.Remove(name);
            process = Process.Start(info) ?? throw new IOException("Could not start the local Hindi language model.");
            ChildProcessJob.Attach(process);
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (System.ComponentModel.Win32Exception) { }
            process.ErrorDataReceived += (_, _) => { }; process.OutputDataReceived += (_, _) => { };
            process.BeginErrorReadLine(); process.BeginOutputReadLine();
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(120))
            {
                token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new IOException("Local Hindi language model exited during startup (GPU memory or driver problem).");
                try { using var health = await http.GetAsync($"http://127.0.0.1:{port}/health", token); if (health.IsSuccessStatusCode) return; }
                catch (HttpRequestException) { }
                await Task.Delay(250, token);
            }
            throw new TimeoutException("Local Hindi language model did not become ready in 120 s.");
        }
        finally { start.Release(); }
    }
    public async Task<(string Text, double Ms, int Tokens)> ChatAsync(string system, string user, int maxTokens, double temperature, CancellationToken token, object? jsonSchema = null)
    {
        await EnsureStartedAsync(token);
        await gate.WaitAsync(token);
        try
        {
            var watch = Stopwatch.StartNew();
            var body = new Dictionary<string, object?>
            {
                ["messages"] = new object[] { new { role = "system", content = system }, new { role = "user", content = user } },
                ["max_tokens"] = maxTokens, ["temperature"] = temperature, ["top_p"] = 0.9, ["stream"] = false, ["cache_prompt"] = true,
                ["chat_template_kwargs"] = new { enable_thinking = false }
            };
            // Grammar-constrained JSON (llama.cpp json_schema): output always parses.
            if (jsonSchema is not null) body["json_schema"] = jsonSchema;
            using var response = await http.PostAsJsonAsync($"http://127.0.0.1:{port}/v1/chat/completions", body, token);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            var tokens = doc.RootElement.TryGetProperty("usage", out var usage) ? usage.GetProperty("completion_tokens").GetInt32() : 0;
            return (text, watch.Elapsed.TotalMilliseconds, tokens);
        }
        finally { gate.Release(); }
    }
    /// <summary>Frees the GPU memory now (game needs it); with GpuGuard.LlmShed set it will not restart.</summary>
    public void Release()
    {
        try { if (process is { HasExited: false }) process.Kill(true); } catch (InvalidOperationException) { }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        try { if (process is { HasExited: false }) process.Kill(true); } catch (InvalidOperationException) { }
        process?.Dispose(); http.Dispose();
    }
}

/// <summary>
/// Context-aware English→spoken-Hindi localisation with a small local LLM. Every output passes a
/// meaning guard (Hindi script, numbers, negation, names, length); a failing line falls back to the
/// sentence MT model so nothing is silently invented.
/// </summary>
public sealed class ConversationalLocalizer(LlamaServer server, ILocalizer fallback, bool concise = true) : ILocalizer
{
    public const string PromptVersion = "dub-hi-v6";
    public string Identity => $"llm-{server.ModelName}:{PromptVersion}:{(concise ? "live" : "story")}";
    public int Fallbacks { get; private set; }
    public string? LastRejection { get; private set; }
    public string? LastMeaning { get; private set; }
    private static readonly Regex Tu = new(@"(?<![\p{L}\p{M}])(?:तू|तूने|तेरा|तेरी|तेरे|तुझे|तुझसे|तुझको)(?![\p{L}\p{M}])");
    private static readonly Regex Tum = new(@"(?<![\p{L}\p{M}])(?:तुम|तुमने|तुम्हारा|तुम्हारी|तुम्हारे|तुम्हें|तुमसे|तुमको)(?![\p{L}\p{M}])");
    private static readonly Regex Aap = new(@"(?<![\p{L}\p{M}])(?:आप|आपने|आपका|आपकी|आपके|आपको|आपसे)(?![\p{L}\p{M}])");
    /// <summary>Rejects lines that mix address forms or contradict the chosen register.</summary>
    public static string? CheckRegister(string hindi, string? register)
    {
        var used = new[] { ("tu", Tu.IsMatch(hindi)), ("tum", Tum.IsMatch(hindi)), ("aap", Aap.IsMatch(hindi)) }.Where(x => x.Item2).Select(x => x.Item1).ToArray();
        if (used.Length > 1) return "mixed address forms " + string.Join("/", used);
        return null;
    }

    public const string System = """
        You are a senior Hindi dubbing writer localising an English video game into natural, modern, spoken Indian Hindi, the way a good Hindi film or game dub sounds.
        Rules:
        - Write everyday conversational Hindi (Hindustani), not textbook or Sanskritised Hindi. Prefer common words: मदद not सहायता, जल्दी not शीघ्र, ज़रूरत not आवश्यकता, इंतज़ार not प्रतीक्षा, निकलना not प्रस्थान, सच में not वास्तव में, शुक्रिया/थैंक्स not धन्यवाद.
        - Keep the exact meaning, every fact, name, number and negation. Never add or drop information. Understand what the English phrase means in context before writing: phrasal verbs and idioms are translated by meaning, not word by word ("cut it out" = "बस कर", "I'm on it" = "मैं देख लेता हूँ", "hang in there" = "हिम्मत रख", "lose them" in a chase = "उन्हें चकमा देना").
        - Keep the speaker's attitude: anger, fear, sarcasm, jokes, slang.
        - Pronouns: use तुम by default. Use तू only for threats, insults, or close street friends joking around; use आप for strangers, elders, bosses, police or polite requests. Never mix तू/तुम/आप forms inside one line, and stay consistent with earlier lines between the same people. Family members and partners use तुम.
        - English words only when Indians really say them in speech (पुलिस, कार, प्लान, फ़ोन, बॉस, ओके). Do not force Hinglish.
        - Keep character and place names, transliterated in Devanagari (Michael = माइकल, Franklin = फ्रैंकलिन). Write numbers as digits.
        - Lines are spoken over the original timing: keep it short and speakable, no longer than needed.
        Answer as JSON:
        "meaning": at most 15 plain-English words saying what the speaker actually means, slang and idioms resolved.
        "register": "tu", "tum" or "aap", the address form you chose.
        "hindi": the dubbed line, Devanagari only, written from "meaning", using only that address form.
        """;
    private static readonly object Schema = new
    {
        type = "object",
        properties = new { meaning = new { type = "string" }, register = new { type = "string", @enum = new[] { "tu", "tum", "aap" } }, hindi = new { type = "string" } },
        required = new[] { "meaning", "register", "hindi" }
    };

    public async Task<LocalizedLine> TranslateAsync(Dialogue line, GameProfile profile, IReadOnlyList<string> history, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (profile.ReviewedLines.TryGetValue(line.English, out var reviewed)) return new(reviewed, line.Emotion, "reviewed-line");
        var user = BuildPrompt(line, profile, history);
        var maxTokens = Math.Clamp(line.English.Length * 3 + 90, 120, 600);
        string? rejection = null;
        foreach (var temperature in new[] { 0.2, 0.0 })
        {
            try
            {
                var (raw, _, _) = await server.ChatAsync(System, user, maxTokens, temperature, token, Schema);
                using var doc = JsonDocument.Parse(raw);
                var hindi = Clean(doc.RootElement.GetProperty("hindi").GetString() ?? "");
                var register = doc.RootElement.TryGetProperty("register", out var r) ? r.GetString() : null;
                LastMeaning = doc.RootElement.TryGetProperty("meaning", out var m) ? m.GetString() : null;
                rejection = Validate(line.English, hindi, profile) ?? CheckRegister(hindi, register);
                if (rejection is null) return new(hindi, line.Emotion, Identity);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or TimeoutException or InvalidOperationException or JsonException && !token.IsCancellationRequested)
            { rejection = "model error: " + e.Message; break; }
        }
        LastRejection = rejection; Fallbacks++;
        var safe = await fallback.TranslateAsync(line, profile, history, token);
        return safe with { Source = safe.Source + ":llm-rejected(" + rejection + ")" };
    }

    public string BuildPrompt(Dialogue line, GameProfile profile, IReadOnlyList<string> history)
    {
        var prompt = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(profile.Setting) && !profile.Setting.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase)) prompt.AppendLine("Game: " + profile.Setting);
        if (profile.Relationships.Count > 0) prompt.AppendLine("Relationships: " + string.Join("; ", profile.Relationships.Select(p => p.Key + ": " + p.Value)));
        var terms = profile.Glossary.Where(p => !string.IsNullOrWhiteSpace(p.Key) && !string.IsNullOrWhiteSpace(p.Value) && Regex.IsMatch(line.English, @"(?<!\w)" + Regex.Escape(p.Key) + @"(?!\w)", RegexOptions.IgnoreCase)).ToArray();
        if (terms.Length > 0) prompt.AppendLine("Required terms: " + string.Join("; ", terms.Select(t => t.Key + " = " + t.Value)));
        if (history.Count > 0)
        {
            prompt.AppendLine("Earlier lines in this scene (English => Hindi already spoken):");
            foreach (var h in history.TakeLast(4)) prompt.AppendLine("  " + h);
        }
        var speaker = string.IsNullOrWhiteSpace(line.Speaker) || line.Speaker == "Unknown" ? "unknown speaker" : line.Speaker;
        // Gender only from the user's character→voice mapping (explicit evidence), never guessed from text.
        if (speaker != "unknown speaker" && (profile.Characters.Keys.Concat(profile.Voices.Keys).Any(k => k.Equals(speaker, StringComparison.OrdinalIgnoreCase))))
        {
            var category = CharacterVoices.Catalog.FirstOrDefault(v => v.Id == profile.VoiceFor(speaker))?.Category;
            if (category is "Male" or "Female") speaker += category == "Male" ? ", male: use masculine verb forms for the speaker" : ", female: use feminine verb forms for the speaker";
        }
        prompt.AppendLine($"Translate the next line ({speaker}{(line.Emotion is { Length: > 0 } e && e != "neutral" ? ", " + e : "")}){(concise ? "; keep it as short as the meaning allows" : "")}:");
        prompt.Append(line.English);
        return prompt.ToString();
    }

    public static string Clean(string raw)
    {
        var text = Regex.Replace(raw, @"<think>.*?</think>", "", RegexOptions.Singleline).Trim();
        var first = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(l => l.Any(c => c is >= 'ऀ' and <= 'ॿ')) ?? text;
        first = Regex.Replace(first, @"^(Hindi|हिंदी|अनुवाद)\s*[:：]\s*", "", RegexOptions.IgnoreCase);
        return first.Trim().Trim('"', '“', '”', '\'', '*', '`').Trim();
    }

    private static readonly Regex EnglishNegation = new(@"\b(?:not|never|no|nobody|nothing|none|nowhere|neither|nor|can't|cannot|won't|don't|doesn't|didn't|isn't|aren't|wasn't|weren't|ain't|haven't|hasn't|hadn't|shouldn't|wouldn't|couldn't|mustn't)\b|n't\b", RegexOptions.IgnoreCase);
    // Whole Devanagari words only (.NET \b treats vowel signs as non-word, so "छूना" would match न\b).
    private static readonly Regex HindiNegation = new(@"(?<![\p{L}\p{M}])(?:नहीं|नहि|नही|मत|ना|न|बिना|मना)(?![\p{L}\p{M}])");
    /// <summary>Returns null when the Hindi is acceptable, otherwise the reason it was rejected.</summary>
    public static string? Validate(string english, string hindi, GameProfile? profile = null)
    {
        if (string.IsNullOrWhiteSpace(hindi)) return "empty";
        var deva = hindi.Count(c => c is >= 'ऀ' and <= 'ॿ'); var latin = hindi.Count(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        if (deva < 2 || latin > deva / 4 + 2) return "not Devanagari Hindi";
        var ratio = hindi.Length / (double)Math.Max(1, english.Length);
        if (english.Length > 12 && (ratio < .35 || ratio > 2.4)) return $"length ratio {ratio:0.00}";
        foreach (Match number in Regex.Matches(english, @"\d+"))
        {
            var devanagari = string.Concat(number.Value.Select(c => (char)('०' + (c - '0'))));
            if (!hindi.Contains(number.Value) && !hindi.Contains(devanagari)) return "number " + number.Value + " missing";
        }
        if (EnglishNegation.IsMatch(english) && !HindiNegation.IsMatch(hindi)) return "negation missing";
        if (profile is not null)
            foreach (var term in profile.Glossary.Where(p => !string.IsNullOrWhiteSpace(p.Value) && Regex.IsMatch(english, @"(?<!\w)" + Regex.Escape(p.Key) + @"(?!\w)", RegexOptions.IgnoreCase)))
                if (!hindi.Contains(term.Value, StringComparison.OrdinalIgnoreCase)) return "glossary term " + term.Key + " missing";
        if (Regex.IsMatch(hindi, @"[A-Za-z]{2,}")) return "Latin text in Hindi line";
        return null;
    }
}
