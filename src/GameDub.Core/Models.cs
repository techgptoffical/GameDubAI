using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameDub.Core;

public record DialogueSample(string Id, string English, string Hindi, string Emotion, string Speaker, string Context);
public record Dialogue(string English, string Speaker = "Unknown", string Emotion = "neutral", string Context = "", double? DurationSeconds = null)
{
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset FirstSeenAt { get; init; } = DateTimeOffset.UtcNow;
}
public record LocalizedLine(string Hindi, string Emotion, string Source);
public record AudioClip(byte[] Bytes, string Format, string Identity);
public record LatencyRecord(DateTimeOffset At, string Source, bool CacheHit, double TranslationMs, double SynthesisMs, double FirstAudioMs, double TotalMs)
{
    public double? ObservedToAudioMs { get; init; }
}
public record CaptureRegion(int X, int Y, int Width, int Height);
public sealed class GameProfile
{
    public string GameId { get; set; } = "default";
    public string Setting { get; set; } = "Unknown; do not invent a setting.";
    public string DefaultVoice { get; set; } = "hi";
    public Dictionary<string, string> Voices { get; set; } = new();
    public Dictionary<string, string> Relationships { get; set; } = new();
    public Dictionary<string, string> Glossary { get; set; } = new();
    public Dictionary<string, string> ReviewedLines { get; set; } = new();
    public CaptureRegion? Region { get; set; }
    public int RegionReferenceWidth { get; set; }
    public int RegionReferenceHeight { get; set; }
    public string OcrStyle { get; set; } = "Raw";
    public int OcrScale { get; set; } = 1;
    public string VoiceFor(string speaker) => Voices.GetValueOrDefault(speaker, DefaultVoice);
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? throw new InvalidDataException("Invalid JSON.");
    public static void Write<T>(string path, T data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(data, Options));
        File.Move(temporary, path, true);
    }
    public static List<DialogueSample> Samples() => Read<List<DialogueSample>>(Path.Combine(AppContext.BaseDirectory, "data", "dialogue-tests.json"));
}

public static partial class Text
{
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
    public static string Clean(string value) => Whitespace().Replace(value.Replace('’', '\''), " ").Trim();
    public static string Key(string value) => Clean(value).ToLowerInvariant();
    public static Dialogue Parse(string text)
    {
        text = Clean(text);
        var colon = text.IndexOf(':');
        if (colon is > 0 and < 32 && text[..colon].All(c => char.IsLetter(c) || c is ' ' or '-'))
            return new(text[(colon + 1)..].Trim(), text[..colon]);
        return new(text);
    }
}

public interface ILocalizer
{
    string Identity { get; }
    Task<LocalizedLine> TranslateAsync(Dialogue line, GameProfile profile, IReadOnlyList<string> history, CancellationToken token);
}
public interface IVoiceProvider
{
    string Identity { get; }
    Task<AudioClip> SynthesizeAsync(LocalizedLine line, string voice, CancellationToken token);
}
public interface IChunkedVoiceProvider : IVoiceProvider
{
    IReadOnlyList<LocalizedLine> DeliveryChunks(LocalizedLine line);
}
public interface IAudioPlayer
{
    Task PlayAsync(AudioClip clip, Action started, CancellationToken token);
    void Stop();
}

public sealed class DialogueContextMemory
{
    private readonly Queue<string> lines = new();
    private readonly object gate = new();
    public IReadOnlyList<string> Snapshot() { lock(gate) return lines.ToArray(); }
    public void Add(Dialogue original, LocalizedLine localized)
    {
        lock(gate)
        {
            lines.Enqueue($"{original.Speaker}: {original.English} => {localized.Hindi}");
            while (lines.Count > 6) lines.Dequeue();
        }
    }
    public void Clear() { lock(gate) lines.Clear(); }
}

public sealed class FixtureLocalizer : ILocalizer
{
    private readonly List<DialogueSample> samples = Json.Samples();
    public string Identity => "curated-fixture-v1-NOT-general-translation";
    public Task<LocalizedLine> TranslateAsync(Dialogue line, GameProfile profile, IReadOnlyList<string> history, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var candidates = samples.Where(s => Text.Key(s.English) == Text.Key(line.English)).ToList();
        var match = candidates.FirstOrDefault(s => s.Context == line.Context) ?? (candidates.Count == 1 ? candidates[0] : null);
        if (match is null) throw new InvalidOperationException(candidates.Count > 1
            ? "This local sample is ambiguous. Select its context in Voice Lab, or use Gemini."
            : "Local fixture mode only knows the bundled sample lines. Configure Gemini for arbitrary game dialogue.");
        return Task.FromResult(new LocalizedLine(match.Hindi, line.Emotion == "neutral" ? match.Emotion : line.Emotion, Identity));
    }
}
