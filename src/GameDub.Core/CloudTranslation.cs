using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GameDub.Core;

/// <summary>
/// Google Gemini (cloud) as the dubbing writer. Opt-in: only the subtitle line, the speaker, the last few
/// lines of the scene and the user's glossary are sent. The API key comes from the user's own Google AI
/// Studio account, entered in GameDub and stored encrypted for this Windows user (DPAPI).
/// Rate limits, a rejected key or no network never stop dubbing: the caller falls back to the local translator.
/// </summary>
public sealed class GeminiClient : IChatModel, IDisposable
{
    public const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/";
    private readonly HttpClient http;
    private readonly Func<string?> key;
    private readonly string? modelOverride;
    private readonly SemaphoreSlim resolve = new(1);
    private string? model;
    private bool thinkingConfigRejected;
    private DateTimeOffset cooldownUntil;
    /// <summary>Why Gemini is paused (rate limit, rejected key, network), shown once to the user.</summary>
    public event Action<string>? Notice;
    public int Requests { get; private set; }
    public string ModelName => model ?? modelOverride ?? "gemini-flash-lite";
    public GeminiClient(Func<string?> key, string? modelOverride = null, HttpMessageHandler? handler = null)
    {
        this.key = key; this.modelOverride = string.IsNullOrWhiteSpace(modelOverride) ? null : modelOverride.Trim();
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(10);
    }
    public bool Configured => !string.IsNullOrWhiteSpace(key());
    public DateTimeOffset CooldownUntil => cooldownUntil;

    private HttpRequestMessage Request(HttpMethod method, string path, object? body = null)
    {
        var message = new HttpRequestMessage(method, Endpoint + path);
        message.Headers.Add("x-goog-api-key", key() ?? throw new InvalidOperationException("Gemini API key is not set."));
        if (body is not null) message.Content = JsonContent.Create(body);
        return message;
    }

    /// <summary>Newest stable Flash-Lite model this key can use (most free requests per day, lowest latency).</summary>
    public async Task<string> ResolveModelAsync(CancellationToken token)
    {
        if (model is not null) return model;
        if (modelOverride is not null) return model = modelOverride.StartsWith("models/") ? modelOverride[7..] : modelOverride;
        await resolve.WaitAsync(token);
        try
        {
            if (model is not null) return model;
            using var response = await SendAsync(() => Request(HttpMethod.Get, "models?pageSize=1000"), token);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var names = doc.RootElement.TryGetProperty("models", out var list)
                ? list.EnumerateArray()
                    .Where(m => m.TryGetProperty("supportedGenerationMethods", out var g) && g.EnumerateArray().Any(x => x.GetString() == "generateContent"))
                    .Select(m => m.GetProperty("name").GetString()!.Replace("models/", "")).ToList()
                : [];
            return model = PickModel(names);
        }
        finally { resolve.Release(); }
    }
    public static string PickModel(IEnumerable<string> names)
    {
        static bool Special(string n) => Regex.IsMatch(n, "tts|image|audio|live|embed|vision|thinking|exp", RegexOptions.IgnoreCase);
        static double Version(string n) => Regex.Match(n, @"gemini-(\d+(?:\.\d+)?)") is { Success: true } m ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        var lite = names.Where(n => n.Contains("flash-lite") && !Special(n)).ToList();
        var stable = lite.Where(n => !n.Contains("preview")).OrderByDescending(Version).ThenBy(n => n.Length).FirstOrDefault();
        return stable ?? lite.OrderByDescending(Version).FirstOrDefault() ?? "gemini-flash-lite-latest";
    }

    public async Task<(string Text, double Ms, int Tokens)> ChatAsync(string system, string user, int maxTokens, double temperature, CancellationToken token, object? jsonSchema = null)
    {
        if (!Configured) throw new InvalidOperationException("Gemini API key is not set.");
        if (DateTimeOffset.UtcNow < cooldownUntil) throw new InvalidOperationException($"Gemini paused until {cooldownUntil.ToLocalTime():HH:mm}; local translator meanwhile.");
        var watch = Stopwatch.StartNew();
        var name = await ResolveModelAsync(token);
        object Body(bool thinking)
        {
            var config = new Dictionary<string, object?> { ["temperature"] = temperature, ["maxOutputTokens"] = Math.Max(maxTokens, 256) };
            if (jsonSchema is not null) { config["responseMimeType"] = "application/json"; config["responseSchema"] = ToGeminiSchema(jsonSchema); }
            if (thinking) config["thinkingConfig"] = new { thinkingBudget = 0 }; // dubbing needs speed, not deliberation
            return new Dictionary<string, object?>
            {
                ["systemInstruction"] = new { parts = new[] { new { text = system } } },
                ["contents"] = new[] { new { role = "user", parts = new[] { new { text = user } } } },
                ["generationConfig"] = config,
                // Mature game dialogue (swearing, threats) must be translated, not refused.
                ["safetySettings"] = new[] { "HARM_CATEGORY_HARASSMENT", "HARM_CATEGORY_HATE_SPEECH", "HARM_CATEGORY_SEXUALLY_EXPLICIT", "HARM_CATEGORY_DANGEROUS_CONTENT" }
                    .Select(c => new { category = c, threshold = "BLOCK_NONE" }).ToArray()
            };
        }
        HttpResponseMessage response;
        try { response = await SendAsync(() => Request(HttpMethod.Post, $"models/{name}:generateContent", Body(!thinkingConfigRejected)), token); }
        catch (GeminiBadRequest e) when (!thinkingConfigRejected && e.Message.Contains("thinking", StringComparison.OrdinalIgnoreCase))
        {
            thinkingConfigRejected = true;
            response = await SendAsync(() => Request(HttpMethod.Post, $"models/{name}:generateContent", Body(false)), token);
        }
        using (response)
        {
            Requests++;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var candidate = doc.RootElement.TryGetProperty("candidates", out var c) && c.GetArrayLength() > 0 ? c[0] : default;
            if (candidate.ValueKind != JsonValueKind.Object || !candidate.TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts))
            {
                var reason = candidate.ValueKind == JsonValueKind.Object && candidate.TryGetProperty("finishReason", out var f) ? f.GetString()
                    : doc.RootElement.TryGetProperty("promptFeedback", out var pf) && pf.TryGetProperty("blockReason", out var br) ? br.GetString() : "empty";
                throw new InvalidOperationException("Gemini returned no text (" + reason + ")");
            }
            var text = string.Concat(parts.EnumerateArray().Where(p => !(p.TryGetProperty("thought", out var t) && t.GetBoolean())).Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : ""));
            var tokens = doc.RootElement.TryGetProperty("usageMetadata", out var u) && u.TryGetProperty("candidatesTokenCount", out var ct) ? ct.GetInt32() : 0;
            return (text, watch.Elapsed.TotalMilliseconds, tokens);
        }
    }

    private sealed class GeminiBadRequest(string message) : InvalidOperationException(message);

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken token)
    {
        HttpResponseMessage response;
        try { using var request = make(); response = await http.SendAsync(request, token); }
        catch (TaskCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("Gemini did not answer within 10 s."); }
        catch (HttpRequestException e) { Pause(TimeSpan.FromSeconds(20), "No connection to Gemini (" + e.Message + "). Local translator for now."); throw; }
        if (response.IsSuccessStatusCode) return response;
        var body = await response.Content.ReadAsStringAsync(token); var status = (int)response.StatusCode; response.Dispose();
        var message = ErrorMessage(body);
        if (status == 429)
        {
            var daily = body.Contains("PerDay", StringComparison.OrdinalIgnoreCase);
            var wait = daily ? TimeSpan.FromHours(1) : RetryDelay(body) ?? TimeSpan.FromSeconds(30);
            Pause(wait, daily ? "Gemini free daily limit reached. Local translator until the limit resets (checked again hourly)."
                              : $"Gemini per-minute limit reached. Local translator for {wait.TotalSeconds:0} s.");
            throw new InvalidOperationException("Gemini rate limit: " + message);
        }
        if (status is 400 or 401 or 403 && Regex.IsMatch(body, "API_KEY_INVALID|API key not valid|PERMISSION_DENIED|API_KEY", RegexOptions.IgnoreCase))
        {
            Pause(TimeSpan.FromMinutes(30), "Gemini rejected the API key. Check it in Settings; local translator meanwhile.");
            throw new InvalidOperationException("Gemini API key rejected: " + message);
        }
        if (status == 400) throw new GeminiBadRequest(message);
        if (status == 404) { model = null; throw new InvalidOperationException("Gemini model not found: " + message); }
        if (status >= 500) Pause(TimeSpan.FromSeconds(15), "Gemini is temporarily unavailable. Local translator for now.");
        throw new InvalidOperationException($"Gemini error {status}: {message}");
    }
    private void Pause(TimeSpan wait, string notice)
    {
        var until = DateTimeOffset.UtcNow + wait;
        if (until > cooldownUntil) { cooldownUntil = until; Notice?.Invoke(notice); }
    }
    private static string ErrorMessage(string body)
    {
        try { using var d = JsonDocument.Parse(body); return d.RootElement.GetProperty("error").GetProperty("message").GetString() ?? body; }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return body.Length > 200 ? body[..200] : body; }
    }
    private static TimeSpan? RetryDelay(string body)
    {
        var m = Regex.Match(body, "\"retryDelay\"\\s*:\\s*\"(\\d+(?:\\.\\d+)?)s\"");
        return m.Success ? TimeSpan.FromSeconds(Math.Clamp(double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 5, 600)) : null;
    }
    /// <summary>JSON Schema (lower-case types) → Gemini responseSchema (upper-case OpenAPI types, no enums).</summary>
    public static JsonNode? ToGeminiSchema(object schema)
    {
        var node = JsonSerializer.SerializeToNode(schema);
        void Walk(JsonNode? n)
        {
            if (n is JsonObject o)
            {
                if (o["type"] is JsonValue t && t.TryGetValue<string>(out var type)) o["type"] = type.ToUpperInvariant();
                o.Remove("enum");
                foreach (var child in o.ToList()) Walk(child.Value);
            }
            else if (n is JsonArray a) foreach (var item in a) Walk(item);
        }
        Walk(node);
        return node;
    }
    public void Dispose() { http.Dispose(); resolve.Dispose(); }
}

/// <summary>The user's Gemini key, encrypted with Windows DPAPI for the current Windows user. Never logged.</summary>
public static class ApiKeyStore
{
    public static string KeyPath { get; set; } = Path.Combine(Paths.Root, "gemini-key.bin");
    public static string? Load()
    {
        try { return File.Exists(KeyPath) ? Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(KeyPath))) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return null; }
    }
    public static void Save(string key)
    {
        key = key.Trim();
        if (key.Length < 20 || key.Any(char.IsWhiteSpace)) throw new ArgumentException("That does not look like a Gemini API key.");
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        File.WriteAllBytes(KeyPath, Protect(Encoding.UTF8.GetBytes(key)));
    }
    public static void Clear() { if (File.Exists(KeyPath)) File.Delete(KeyPath); }

    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr handle);
    private const int UiForbidden = 0x1;
    private static byte[] Protect(byte[] data) => Crypt(data, true);
    private static byte[] Unprotect(byte[] data) => Crypt(data, false);
    private static byte[] Crypt(byte[] data, bool protect)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new Blob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
            var ok = protect ? CryptProtectData(ref input, "GameDub Gemini key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                             : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try { var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, output.Size); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { handle.Free(); }
    }
}
