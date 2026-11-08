using System.Net;
using System.Text;
using GameDub.Core;

/// <summary>Gemini translator against a fake Google endpoint: no network, no real key.</summary>
internal static class GeminiTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private const string FakeKey = "AIzaFAKE-test-key-0000000000000000000";

    private sealed class FakeGoogle(Func<HttpRequestMessage, string, (HttpStatusCode, string)> reply) : HttpMessageHandler
    {
        public readonly List<(string Url, string? Key, string Body)> Calls = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
            Calls.Add((request.RequestUri!.ToString(), request.Headers.TryGetValues("x-goog-api-key", out var k) ? k.First() : null, body));
            var (status, text) = reply(request, body);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }
    private const string Models = """{"models":[{"name":"models/gemini-2.5-flash","supportedGenerationMethods":["generateContent"]},{"name":"models/gemini-2.5-flash-lite","supportedGenerationMethods":["generateContent"]},{"name":"models/gemini-2.5-flash-preview-tts","supportedGenerationMethods":["generateContent"]}]}""";
    private static string Answer(string hindi) => "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":" + System.Text.Json.JsonSerializer.Serialize("{\"meaning\":\"that is the wrong guy\",\"register\":\"tum\",\"hindi\":\"" + hindi + "\"}") + "}]}}]}";

    public static async Task Run(string root, Func<string, Func<Task>, Task> test)
    {
        await test("Gemini picks the newest stable Flash-Lite model and gets a Gemini-style JSON schema", () =>
        {
            Check(GeminiClient.PickModel(["gemini-2.0-flash-lite", "gemini-2.5-flash-lite", "gemini-3.1-flash-lite-preview", "gemini-2.5-flash-preview-tts", "gemini-2.5-flash"]) == "gemini-2.5-flash-lite", "Wrong model chosen");
            Check(GeminiClient.PickModel(["gemini-3.1-flash-lite-preview"]) == "gemini-3.1-flash-lite-preview", "Preview-only list not used");
            Check(System.Text.Json.JsonSerializer.Serialize(GeminiClient.ThinkingConfig("gemini-3.5-flash-lite")).Contains("minimal")
                && System.Text.Json.JsonSerializer.Serialize(GeminiClient.ThinkingConfig("gemini-2.5-flash-lite")).Contains("thinkingBudget"), "Wrong thinking setting per model generation");
            var schema = GeminiClient.ToGeminiSchema(ConversationalLocalizer.Schema)!.ToJsonString();
            Check(schema.Contains("\"OBJECT\"") && schema.Contains("\"STRING\"") && !schema.Contains("enum"), "Schema not converted: " + schema);
            return Task.CompletedTask;
        });
        await test("Gemini dubbing: natural Hindi through the meaning guard, key only in the header", async () =>
        {
            var google = new FakeGoogle((r, _) => r.Method == HttpMethod.Get ? (HttpStatusCode.OK, Models) : (HttpStatusCode.OK, Answer("गलत घर है। ये हमारा बंदा नहीं है।")));
            using var client = new GeminiClient(() => FakeKey, null, google);
            var localizer = new ConversationalLocalizer(client, new FixtureLocalizer());
            var line = await localizer.TranslateAsync(new("Wrong house. This guy isn't our guy.", "Michael"), new GameProfile(), ["Go to the house. => घर जाओ।"], default);
            Check(line.Hindi == "गलत घर है। ये हमारा बंदा नहीं है।" && line.Source.Contains("gemini-2.5-flash-lite"), "Gemini answer not used: " + line.Hindi + " / " + line.Source);
            var post = google.Calls.Last();
            Check(post.Url.EndsWith("models/gemini-2.5-flash-lite:generateContent") && post.Key == FakeKey && google.Calls.All(c => !c.Url.Contains(FakeKey)), "Key in URL or wrong endpoint");
            Check(post.Body.Contains("systemInstruction") && post.Body.Contains("BLOCK_NONE") && post.Body.Contains("Go to the house."), "Prompt, scene context or safety settings missing");

            // A model that rejects the thinking setting with a vague "invalid argument" is retried once without it, then remembered.
            var strict = new FakeGoogle((r, body) => r.Method == HttpMethod.Get ? (HttpStatusCode.OK, Models)
                : body.Contains("thinking") ? (HttpStatusCode.BadRequest, "{\"error\":{\"code\":400,\"message\":\"Request contains an invalid argument.\"}}") : (HttpStatusCode.OK, Answer("चलो, निकलते हैं।")));
            using var strictClient = new GeminiClient(() => FakeKey, null, strict);
            var strictLocalizer = new ConversationalLocalizer(strictClient, new FixtureLocalizer());
            var first = await strictLocalizer.TranslateAsync(new("Let's go."), new GameProfile(), [], default);
            var posts = strict.Calls.Count(c => c.Url.Contains("generateContent"));
            await strictLocalizer.TranslateAsync(new("Let's move."), new GameProfile(), [], default);
            Check(first.Hindi == "चलो, निकलते हैं।" && posts == 2 && strict.Calls.Count(c => c.Url.Contains("generateContent")) == 3, "Invalid thinking setting not retried/remembered");
        });
        await test("Gemini lost negation, rate limit and rejected key all fall back to the local translator", async () =>
        {
            var answer = Answer("गलत घर है। ये हमारा बंदा है।"); // drops "isn't"
            var google = new FakeGoogle((r, _) => r.Method == HttpMethod.Get ? (HttpStatusCode.OK, Models) : (HttpStatusCode.OK, answer));
            using var client = new GeminiClient(() => FakeKey, null, google);
            var fallback = new CountingFallback(); var localizer = new ConversationalLocalizer(client, fallback);
            var line = await localizer.TranslateAsync(new("Wrong house. This guy isn't our guy."), new GameProfile(), [], default);
            Check(fallback.Count == 1 && line.Source.Contains("negation missing"), "Meaning guard skipped for Gemini");

            var notices = new List<string>(); var limited = 0;
            var busy = new FakeGoogle((r, _) => r.Method == HttpMethod.Get ? (HttpStatusCode.OK, Models)
                : (HttpStatusCode.TooManyRequests, "{\"error\":{\"code\":429,\"message\":\"Quota exceeded\",\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.RetryInfo\",\"retryDelay\":\"40s\"}]}}"));
            using var limitedClient = new GeminiClient(() => FakeKey, null, busy); limitedClient.Notice += notices.Add;
            var limitedLocalizer = new ConversationalLocalizer(limitedClient, fallback);
            await limitedLocalizer.TranslateAsync(new("Go to the house."), new GameProfile(), [], default);
            limited = busy.Calls.Count(c => c.Url.Contains("generateContent"));
            await limitedLocalizer.TranslateAsync(new("Get me another address."), new GameProfile(), [], default);
            Check(fallback.Count == 3 && busy.Calls.Count(c => c.Url.Contains("generateContent")) == limited, "Kept calling Gemini during the cooldown");
            Check(notices.Count == 1 && notices[0].Contains("40 s") && limitedClient.CooldownUntil > DateTimeOffset.UtcNow.AddSeconds(30), "Rate limit not explained: " + string.Join(" | ", notices));

            var badKey = new FakeGoogle((_, _) => (HttpStatusCode.BadRequest, "{\"error\":{\"code\":400,\"message\":\"API key not valid. Please pass a valid API key.\",\"status\":\"INVALID_ARGUMENT\",\"details\":[{\"reason\":\"API_KEY_INVALID\"}]}}"));
            using var badClient = new GeminiClient(() => FakeKey, null, badKey); notices.Clear(); badClient.Notice += notices.Add;
            await new ConversationalLocalizer(badClient, fallback).TranslateAsync(new("Go to the house."), new GameProfile(), [], default);
            Check(fallback.Count == 4 && notices.Any(n => n.Contains("API key")), "Rejected key not reported");
        });
        await test("Gemini key is stored encrypted, never as plain text", () =>
        {
            var saved = ApiKeyStore.KeyPath;
            try
            {
                ApiKeyStore.KeyPath = Path.Combine(root, "gemini-key-test.bin");
                ApiKeyStore.Save(FakeKey);
                Check(!Encoding.UTF8.GetString(File.ReadAllBytes(ApiKeyStore.KeyPath)).Contains("AIza"), "Key written as plain text");
                Check(ApiKeyStore.Load() == FakeKey, "Key did not round-trip");
                ApiKeyStore.Clear(); Check(ApiKeyStore.Load() is null, "Key not removed");
                var rejected = false; try { ApiKeyStore.Save("hello"); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "Accepted something that is not a key");
            }
            finally { ApiKeyStore.KeyPath = saved; }
            return Task.CompletedTask;
        });
    }
    private sealed class CountingFallback : ILocalizer
    {
        public int Count;
        public string Identity => "test-local";
        public Task<LocalizedLine> TranslateAsync(Dialogue line, GameProfile profile, IReadOnlyList<string> history, CancellationToken token)
        { Count++; return Task.FromResult(new LocalizedLine("स्थानीय अनुवाद", line.Emotion, Identity)); }
    }
}
