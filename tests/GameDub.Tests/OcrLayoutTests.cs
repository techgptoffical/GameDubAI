using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using GameDub.Core;

// Multi-row subtitle recall on rendered 1080p GTA-style frames (outlined white text, bottom-anchored,
// centred, ~45 px row pitch) over varied scenes with HUD/menu clutter, plus the real clipped GTA crops.
// Synthetic frames are not proof of real-game recall; the real-crop checks are reported separately.
internal static class OcrLayoutTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private const int W = 1920, H = 1080, Bottom = 1062, Pitch = 45;

    private static void Background(Graphics g, string scene, int seed)
    {
        var random = new Random(seed);
        switch (scene)
        {
            case "dark": g.Clear(Color.FromArgb(18, 22, 28)); break;
            case "bright":
                using (var sky = new LinearGradientBrush(new Rectangle(0, 0, W, H), Color.FromArgb(235, 240, 245), Color.FromArgb(170, 190, 205), 90f)) g.FillRectangle(sky, 0, 0, W, H);
                break;
            default: // "busy": street-like blocks, light poles and noise
                g.Clear(Color.FromArgb(90, 95, 100));
                for (var i = 0; i < 160; i++)
                {
                    using var brush = new SolidBrush(Color.FromArgb(random.Next(40, 230), random.Next(40, 220), random.Next(40, 210)));
                    g.FillRectangle(brush, random.Next(-100, W), random.Next(-100, H), random.Next(20, 400), random.Next(10, 260));
                }
                break;
        }
    }

    private static void Hud(Graphics g)
    {
        // Minimap block + health bars at bottom-left, and a pause-menu style button legend at bottom-right.
        using var map = new SolidBrush(Color.FromArgb(200, 40, 60, 70)); g.FillRectangle(map, 28, 800, 272, 240);
        using var small = new Font("Arial", 14, FontStyle.Bold);
        g.DrawString("Vinewood Blvd", small, Brushes.White, 60, 1000);
        using var legend = new Font("Arial", 15);
        g.DrawString("Select   Back   Zoom   Hide Menu", legend, Brushes.White, 1500, 1040);
    }

    public static Bitmap Render(string[] rows, string scene, float alpha = 1, bool hud = true, int seed = 7)
    {
        var bitmap = new Bitmap(W, H);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAlias;
        Background(g, scene, seed);
        if (hud) Hud(g);
        using var family = new FontFamily("Arial");
        using var outline = new Pen(Color.FromArgb((int)(255 * alpha), 0, 0, 0), 4) { LineJoin = LineJoin.Round };
        using var fill = new SolidBrush(Color.FromArgb((int)(255 * alpha), 240, 240, 240));
        for (var i = 0; i < rows.Length; i++)
        {
            using var path = new GraphicsPath();
            var baseline = Bottom - (rows.Length - 1 - i) * Pitch;
            path.AddString(rows[i], family, (int)FontStyle.Regular, 31, new PointF(0, 0), StringFormat.GenericTypographic);
            var bounds = path.GetBounds();
            using var move = new Matrix(); move.Translate(W / 2f - bounds.Width / 2 - bounds.X, baseline - 29);
            path.Transform(move);
            g.DrawPath(outline, path); g.FillPath(fill, path);
        }
        return bitmap;
    }

    private static Bitmap Crop(Bitmap frame, CaptureRegion r)
    {
        var crop = new Bitmap(r.Width, r.Height);
        using var g = Graphics.FromImage(crop); g.DrawImage(frame, new Rectangle(0, 0, r.Width, r.Height), new Rectangle(r.X, r.Y, r.Width, r.Height), GraphicsUnit.Pixel);
        return crop;
    }

    private static double WordRecall(string expected, string actual)
    {
        static string[] Words(string s) => new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c == ' ' ? c : ' ').ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var want = Words(expected); var got = Words(actual).ToList(); var hit = 0;
        foreach (var w in want) { var i = got.IndexOf(w); if (i >= 0) { hit++; got.RemoveAt(i); } }
        return want.Length == 0 ? 1 : hit / (double)want.Length;
    }

    public static async Task Run(string root, Func<string, Func<Task>, Task> test)
    {
        var output = Path.Combine(root, "ocr-layout"); Directory.CreateDirectory(output);
        var region = SubtitleImages.GtaSubtitleRegion(W, H, H - 1);
        var cases = new (string Name, string[] Rows)[]
        {
            ("very-short", ["Yes."]),
            ("short-reply", ["No way."]),
            ("long-single", ["You think I came all the way out here just to watch you mess this up?"]),
            ("two-row", ["We need to get out of here before", "those guys come looking for us!"]),
            ("two-row-question", ["Michael, are you seriously going", "to walk in there alone again?"]),
            ("three-row", ["My practice is closed now, but you can tune", "into my new radio show for all your", "mental health needs."]),
            ("punctuation", ["What?! No... wait, Lamar, are you serious?!"]),
            ("numbers", ["The van leaves at 9:30, and we split it 60-40."]),
            ("names", ["Tell Trevor that Franklin is waiting at Grove Street."]),
            ("negation", ["I'm not going anywhere, and you can't make me."]),
        };
        var report = new List<object>();
        var failures = new List<string>();
        var ocr = new WindowsSubtitleOcr(Environment.GetEnvironmentVariable("GAMEDUB_OCR_STYLE") ?? "Auto", 1, "Center");
        using (var warm = Render(["Ready."], "dark")) using (var warmCrop = Crop(warm, region)) await ocr.RecognizeFrameAsync(warmCrop, default);

        await test("multi-row GTA-style captions reach translation complete (bright/dark/busy, HUD present)", async () =>
        {
            var timings = new List<double>();
            foreach (var scene in new[] { "dark", "bright", "busy" })
                foreach (var (name, rows) in cases)
                {
                    using var frame = Render(rows, scene, seed: name.Length * 31);
                    using var crop = Crop(frame, region);
                    var watch = Stopwatch.StartNew();
                    var seen = await ocr.RecognizeFrameAsync(crop, default, W / 2.0 - region.X);
                    timings.Add(watch.Elapsed.TotalMilliseconds);
                    var expected = string.Join(" ", rows);
                    var recall = WordRecall(expected, seen.Text);
                    var leaked = new[] { "vinewood", "zoom", "hide menu" }.Where(h => seen.Text.Contains(h, StringComparison.OrdinalIgnoreCase)).ToArray();
                    var ok = recall >= .9 && seen.Kept.Count == rows.Length && leaked.Length == 0 && !seen.TopClipped;
                    report.Add(new { scene, name, rows = rows.Length, expected, actual = seen.Text, recall, keptRows = seen.Kept.Count, rejected = seen.Rejected.Select(r => r.Text), seen.TopClipped, leaked, ocrMs = watch.Elapsed.TotalMilliseconds, ok });
                    if (!ok) { failures.Add($"{scene}/{name}: '{seen.Text}' recall {recall:0.00} rows {seen.Kept.Count}/{rows.Length} leaked [{string.Join(",", leaked)}] clipped {seen.TopClipped}"); crop.Save(Path.Combine(output, $"fail-{scene}-{name}.png")); }
                }
            var total = cases.Length * 3;
            Json.Write(Path.Combine(output, "recall.json"), new { region, total, passed = total - failures.Count, failures, p50OcrMs = Percentile(timings, .5), p95OcrMs = Percentile(timings, .95), cases = report });
            Check(failures.Count <= 1, $"Multi-row recall {total - failures.Count}/{total}: " + string.Join(" | ", failures));
        });

        await test("old 72 px strip is detected as clipping and grown until both rows are read", async () =>
        {
            using var frame = Render(["We need to get out of here before", "those guys come looking for us!"], "busy");
            var strip = new CaptureRegion(341, 1007, 1314, 72); var grows = 0; SubtitleFrame seen;
            while (true)
            {
                using var crop = Crop(frame, strip);
                seen = await ocr.RecognizeFrameAsync(crop, default, W / 2.0 - strip.X);
                if (!seen.TopClipped) break;
                var grown = SubtitleLayout.Grow(strip, W, H, true, false); Check(grown is not null, "Could not grow clipped region");
                strip = grown!; grows++; Check(grows < 8, "Region growth did not converge");
            }
            Check(grows >= 1, "Clipping of the upper row was not detected");
            Check(seen.Text.StartsWith("We need to get out", StringComparison.OrdinalIgnoreCase) && seen.Text.Contains("looking for us"), "After growth OCR saw: " + seen.Text);
            Json.Write(Path.Combine(output, "growth.json"), new { grows, finalRegion = strip, seen.Text });
        });

        await test("fade-in and rapid caption changes emit each complete caption once", async () =>
        {
            var detector = new SubtitleChangeDetector(); var emitted = new List<string>(); var clock = TimeSpan.Zero;
            async Task Show(string[] rows, float alpha, int frames)
            {
                using var frame = Render(rows, "busy", alpha); using var crop = Crop(frame, region);
                var seen = await ocr.RecognizeFrameAsync(crop, default, W / 2.0 - region.X);
                for (var i = 0; i < frames; i++) { var o = detector.Observe(seen.Text, clock); if (o.Event == SubtitleEvent.Line) emitted.Add(o.Text); clock += TimeSpan.FromMilliseconds(100); }
            }
            string[] first = ["We need to get out of here before", "those guys come looking for us!"];
            foreach (var a in new[] { .15f, .35f, .6f }) await Show(first, a, 1);
            await Show(first, 1, 6);
            await Show(["Go!"], 1, 4);                     // rapid change, very short
            await Show(["Franklin, take the wheel.", "I'll cover the back."], 1, 4);
            await Show([], 1, 9);                          // cleared
            Check(emitted.Count(e => e.Contains("looking for us")) == 1 && emitted.Any(e => e.StartsWith("We need", StringComparison.OrdinalIgnoreCase) && e.Contains("looking for us")), "Fade-in caption: " + string.Join(" | ", emitted));
            Check(emitted.Any(e => e.Contains("Go", StringComparison.OrdinalIgnoreCase)) && emitted.Any(e => e.Contains("cover the back") && e.Contains("Franklin")), "Rapid change missed: " + string.Join(" | ", emitted));
            Json.Write(Path.Combine(output, "fade-rapid.json"), emitted);
        });

        await test("late upper row supersedes the lower-row-only capture", () =>
        {
            var d = new SubtitleChangeDetector();
            d.Observe("those guys come looking for us!", TimeSpan.Zero);
            Check(d.Observe("those guys come looking for us!", TimeSpan.FromMilliseconds(200)).Event == SubtitleEvent.None, "Lower-case lower row emitted before the upper row could appear");
            var o = d.Observe("those guys come looking for us!", TimeSpan.FromMilliseconds(600));
            Check(o.Event == SubtitleEvent.Line, "Lower row never emitted");
            d.Observe("We need to get out of here before those guys come looking for us!", TimeSpan.FromMilliseconds(700));
            var full = d.Observe("We need to get out of here before those guys come looking for us!", TimeSpan.FromMilliseconds(900));
            Check(full.Event == SubtitleEvent.Line && full.Extends, "Complete caption not flagged as superseding the partial");
            Check(SubtitleChangeDetector.Similar("Dave, ortom Leave a message.", "Dave Norton. Leave a message.") && !SubtitleChangeDetector.Similar("The guy's your neighbor.", "The guy is not your neighbor."), "OCR-noise similarity wrong");
            return Task.CompletedTask;
        });

        await test("button legends and pause-menu rows are not dialogue", () =>
        {
            foreach (var t in new[] { "Select Back Zoom Hide Menu", "Place Waypoint Zoom Legend Back", "Map Brief Stats Settings Game Online Friends", "Browse Rockstar Games Account Caps Hide Menu Back" })
                Check(SubtitleUiFilter.IsControlText(t), "Menu row treated as dialogue: " + t);
            foreach (var t in new[] { "Go back to the map.", "Hold on, let me select a car.", "Zoom in on that guy.", "Back off!" })
                Check(!SubtitleUiFilter.IsControlText(t), "Dialogue rejected: " + t);
            return Task.CompletedTask;
        });

        var real = Path.Combine(Paths.Root, "diagnostic-sessions", "20261108-145003-498");
        if (File.Exists(Path.Combine(real, "crop-0031.png")))
            await test("real GTA trace: clipped two-row crop is flagged, single-row crop is not", async () =>
            {
                using var clipped = new Bitmap(Path.Combine(real, "crop-0031.png"));
                using var single = new Bitmap(Path.Combine(real, "crop-0028.png"));
                var center = W / 2.0 - 341; // real region X=341
                var a = await ocr.RecognizeFrameAsync(clipped, default, center); var b = await ocr.RecognizeFrameAsync(single, default, center);
                Json.Write(Path.Combine(output, "real-gta-crops.json"), new { clipped = new { a.Text, a.TopClipped }, single = new { b.Text, b.TopClipped } });
                Check(a.TopClipped, "Real clipped GTA caption not detected; OCR: " + a.Text);
                Check(!b.TopClipped && b.Text.Contains("Friedlander"), "Single-row GTA caption wrongly flagged or misread: " + b.Text);
            });
    }

    private static double Percentile(List<double> values, double p) { if (values.Count == 0) return 0; var s = values.OrderBy(v => v).ToArray(); return s[(int)Math.Min(s.Length - 1, Math.Floor(p * s.Length))]; }
}
