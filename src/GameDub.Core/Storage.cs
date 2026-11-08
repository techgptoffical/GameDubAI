using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameDub.Core;

public static class Paths
{
    public static string Workspace { get; } = FindWorkspace();
    public static string Root { get; } = Path.Combine(Workspace, "local-settings");
    public static string Profiles => Path.Combine(Root, "profiles");
    private static string FindWorkspace()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "workers")) && Directory.Exists(Path.Combine(dir.FullName, "models"))) return dir.FullName;
        return AppContext.BaseDirectory;
    }
}

public sealed class AppSettings
{
    public string Translator { get; set; } = "Conversational Hindi (local AI, recommended)";
    public string Tts { get; set; } = "Kokoro Hindi (local neural)";
    public string OutputDevice { get; set; } = "";
    public float Volume { get; set; } = 0.7f;
    public int MaxAgeMs { get; set; } = 6000;
    public string PerformanceMode { get; set; } = "Balanced";
    public string InputMode { get; set; } = "Subtitles (recommended)";
    public string DialoguePolicy { get; set; } = "Live Cinematic (recommended)";
    public bool DuckGameAudio { get; set; }
    public float DuckLevel { get; set; } = 0.3f;
    public int SettingsVersion { get; set; }
    public static AppSettings Load()
    {
        AppSettings settings;
        try { settings = Json.Read<AppSettings>(Path.Combine(Paths.Root, "settings.json")); }
        catch (Exception e) when (e is IOException or JsonException) { return new() { SettingsVersion = 2 }; }
        if (settings.SettingsVersion < 2)
        {
            // One-time move to the repaired defaults: conversational Hindi + Live Cinematic. Voice/output kept.
            settings.SettingsVersion = 2;
            if (!settings.Translator.StartsWith("Local sample")) settings.Translator = "Conversational Hindi (local AI, recommended)";
            settings.DialoguePolicy = "Live Cinematic (recommended)";
            try { settings.Save(); } catch (IOException) { }
        }
        return settings;
    }
    public void Save() => Json.Write(Path.Combine(Paths.Root, "settings.json"), this);
}

public sealed class DialogueCache
{
    private static readonly object gate = new();
    private readonly string directory;
    private readonly long maxBytes;
    public DialogueCache(string? directory = null, long maxBytes = 128 * 1024 * 1024)
    {
        this.directory = directory ?? Path.Combine(Paths.Root, "cache");
        this.maxBytes = maxBytes;
        Directory.CreateDirectory(this.directory);
    }
    public static string Identity(Dialogue line, GameProfile profile, string localizer, string tts, string voice, IReadOnlyList<string> history)
    {
        var payload = JsonSerializer.Serialize(new { version = 5, line.English, line.Speaker, line.Emotion, line.Context, line.DurationSeconds, profile.GameId, profile.Setting, profile.Glossary, profile.Relationships, profile.ReviewedLines,profile.ConversationalHindi, localizer, tts, voice, history });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
    public (LocalizedLine Line, AudioClip Clip)? Get(string key)
    {
        lock(gate)
        {
        var path = Path.Combine(directory, key + ".json");
        try
        {
            if (!File.Exists(path)) return null;
            var item = Json.Read<Entry>(path);
            if(item.Line is null || item.Clip?.Bytes is null) return null;
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return (item.Line, item.Clip);
        }
        catch (Exception e) when (e is IOException or JsonException or FormatException) { return null; }
        }
    }
    public void Put(string key, LocalizedLine line, AudioClip clip)
    {
        lock(gate)
        {
        if (clip.Bytes.Length > maxBytes / 2) return;
        Json.Write(Path.Combine(directory, key + ".json"), new Entry(line, clip));
        var files = new DirectoryInfo(directory).GetFiles("*.json").OrderBy(f => f.LastWriteTimeUtc).ToList();
        var total = files.Sum(f => f.Length);
        foreach (var file in files) { if (total <= maxBytes) break; total -= file.Length; file.Delete(); }
        }
    }
    private record Entry(LocalizedLine Line, AudioClip Clip);
}

public sealed class LatencyMetrics
{
    private readonly Queue<LatencyRecord> records = new();
    public void Add(LatencyRecord item)
    {
        lock (records) { records.Enqueue(item); while (records.Count > 1000) records.Dequeue(); }
    }
    public LatencyRecord[] Snapshot() { lock (records) return records.ToArray(); }
    public string Summary()
    {
        var items = Snapshot().Select(r => r.ObservedToAudioMs ?? r.FirstAudioMs).Order().ToArray();
        if (items.Length == 0) return "No playback-start measurements yet.";
        double P(double percentile) => items[Math.Clamp((int)Math.Ceiling(items.Length * percentile) - 1, 0, items.Length - 1)];
        return $"First observation → playback request • n={items.Length} • median {P(.5):0} ms • p95 {P(.95):0} ms • worst {items[^1]:0} ms\nCapture observation is not exact screen onset. Voice lab starts at submission. Acoustic delay is not included.";
    }
}
