using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;

namespace GameDub.Core;

public sealed record TraceFrame(double AtMs, string File, string Ocr, double OcrMs, double? Confidence = null)
{
    public string? Seen { get; init; }
    public string[]? Rows { get; init; }
    public string[]? Rejected { get; init; }
    public bool TopClipped { get; init; }
}
public sealed record TraceEntry(DateTimeOffset At, double AtMs, string Kind, string? DialogueId, object? Data);

// Explicit, local debugging only. No full-screen images or audio. Stops saving crops
// after two minutes / 120 images / 16 MiB; event log is independently capped.
public sealed class PipelineTrace : IDisposable
{
    private readonly object gate = new();
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private readonly StreamWriter log, frames;
    private bool disposed;
    private int events, images;
    private long bytes;
    private double lastFrameAt = -1000;
    private string lastOcr = "\0";
    public string DirectoryPath { get; }
    public string? Error { get; private set; }
    public PipelineTrace(string? directory = null)
    {
        DirectoryPath = directory ?? Path.Combine(Paths.Root, "diagnostic-sessions", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(DirectoryPath);
        log = new(Path.Combine(DirectoryPath, "events.jsonl"), false, System.Text.Encoding.UTF8) { AutoFlush = true };
        frames = new(Path.Combine(DirectoryPath, "frames.jsonl"), false, System.Text.Encoding.UTF8) { AutoFlush = true };
        Write("session", null, new { cropLimit = 120, cropMiBLimit = 16, cropSecondsLimit = 120,
            firstSeenMeaning = "First captured observation, not exact physical display onset", ocrConfidence = "Windows OCR does not expose calibrated confidence", audio = "No audio stored; playback timestamps are WASAPI requests, not acoustic arrival" });
    }
    public void Write(string kind, Dialogue? line = null, object? data = null)
    {
        lock(gate)
        {
            if(disposed || Error is not null || events++ >= 5000) return;
            try { log.WriteLine(JsonSerializer.Serialize(new TraceEntry(DateTimeOffset.UtcNow, watch.Elapsed.TotalMilliseconds, kind, line?.EventId, data))); }
            catch(IOException e) { Error = e.Message; }
        }
    }
    public void Frame(Bitmap crop, string ocr, double ocrMs, SubtitleFrame? seen = null)
    {
        lock(gate)
        {
            var at = watch.Elapsed.TotalMilliseconds;
            if(disposed || Error is not null || images >= 120 || bytes >= 16*1024*1024 || at > 120000) return;
            if(ocr == lastOcr && at-lastFrameAt < 500) return;
            try
            {
                using var buffer = new MemoryStream(); crop.Save(buffer, ImageFormat.Png);
                if(bytes + buffer.Length > 16*1024*1024) return;
                var name = $"crop-{images++:0000}.png";
                File.WriteAllBytes(Path.Combine(DirectoryPath,name),buffer.ToArray()); bytes += buffer.Length;
                string? seenName = null;
                if(seen is { } f && (f.Kept.Count>0 || f.Rejected.Count>0 || f.TopClipped))
                {
                    // "What OCR sees": green = rows sent to translation, red = rejected HUD/menu rows, orange top bar = clipped.
                    using var overlay = new Bitmap(crop); using var g = Graphics.FromImage(overlay);
                    using var keep = new Pen(Color.Lime, 2); using var drop = new Pen(Color.Red, 2);
                    foreach(var l in f.Kept) g.DrawRectangle(keep, l.Box.X, l.Box.Y, l.Box.Width, l.Box.Height);
                    foreach(var l in f.Rejected) g.DrawRectangle(drop, l.Box.X, l.Box.Y, l.Box.Width, l.Box.Height);
                    if(f.TopClipped) g.FillRectangle(Brushes.Orange, 0, 0, overlay.Width, 3);
                    using var seenBuffer = new MemoryStream(); overlay.Save(seenBuffer, ImageFormat.Png);
                    if(bytes + seenBuffer.Length <= 16*1024*1024) { seenName = $"seen-{images-1:0000}.png"; File.WriteAllBytes(Path.Combine(DirectoryPath,seenName),seenBuffer.ToArray()); bytes += seenBuffer.Length; }
                }
                frames.WriteLine(JsonSerializer.Serialize(new TraceFrame(at,name,ocr,ocrMs) { Seen = seenName, Rows = seen?.Kept.Select(l=>l.Text).ToArray(), Rejected = seen?.Rejected.Select(l=>l.Text).ToArray(), TopClipped = seen?.TopClipped ?? false }));
                lastOcr = ocr; lastFrameAt = at;
            }
            catch(IOException e) { Error = e.Message; }
        }
    }
    public void Dispose()
    {
        lock(gate)
        {
            if(disposed) return;
            Write("session_end", null, new { images, bytes, events, elapsedMs = watch.Elapsed.TotalMilliseconds });
            disposed = true; frames.Dispose(); log.Dispose();
        }
    }
}

public static class DiagnosticReplay
{
    public static TraceFrame[] Frames(string directory) => File.ReadLines(Path.Combine(directory,"frames.jsonl"))
        .Where(s=>!string.IsNullOrWhiteSpace(s)).Take(5000).Select(s=>JsonSerializer.Deserialize<TraceFrame>(s,Json.Options)!).ToArray();
    public static string CropPath(string directory, string name)
    {
        if(Path.GetFileName(name) != name || !name.EndsWith(".png",StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Replay crop must be a local PNG filename.");
        return Path.Combine(directory,name);
    }
    // Replays every recorded OCR observation at its captured timing; images are
    // sampled separately and can be re-OCRed for inspection without a running game.
    public static async Task ReplayOcrAsync(string directory, IDialogueSink sink, CancellationToken token)
    {
        var detector = new SubtitleChangeDetector(); var clock = Stopwatch.StartNew(); double? origin = null; string? lastId = null;
        foreach(var row in File.ReadLines(Path.Combine(directory,"events.jsonl")))
        {
            using var doc = JsonDocument.Parse(row);
            var kind=doc.RootElement.GetProperty("Kind").GetString();
            if(kind != "ocr" && kind != "scene_interrupt") continue;
            var at = doc.RootElement.GetProperty("AtMs").GetDouble(); origin ??= at;
            var elapsed = TimeSpan.FromMilliseconds(at-origin.Value);
            var wait = elapsed-clock.Elapsed; if(wait > TimeSpan.Zero) await Task.Delay(wait,token);
            token.ThrowIfCancellationRequested();
            if(kind=="scene_interrupt") { sink.Interrupt(); detector=new(); continue; }
            var raw = doc.RootElement.GetProperty("Data").GetProperty("text").GetString() ?? "";
            var observation = detector.Observe(raw,elapsed);
            if(observation.Event == SubtitleEvent.Line) { var line=Text.Parse(observation.Text) with { Supersedes=observation.Extends?lastId:null };lastId=line.EventId;sink.Submit(line); }
            else if(observation.Event == SubtitleEvent.Cleared) sink.SubtitleCleared();
        }
    }
}
