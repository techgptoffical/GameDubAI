using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GameDub.Core;

public sealed record OcrWord(string Text, RectangleF Box);
public sealed record OcrTextLine(string Text, RectangleF Box, IReadOnlyList<OcrWord> Words);
/// <summary>What OCR saw in one crop after layout analysis. Text is the full subtitle block in reading order.</summary>
public sealed record SubtitleFrame(string Text, IReadOnlyList<OcrTextLine> Kept, IReadOnlyList<OcrTextLine> Rejected, bool TopClipped, bool BottomClipped, bool SideClipped, string Variant)
{
    public static readonly SubtitleFrame Empty = new("", [], [], false, false, false, "Raw");
}

/// <summary>
/// Turns Windows OCR lines into one subtitle event: drops HUD/menu text by position, keeps the
/// bottom-most block of adjacent rows (game subtitles grow upward), joins rows top-to-bottom and
/// reports when the crop edge cuts through subtitle glyphs so the region can be grown.
/// </summary>
public static class SubtitleLayout
{
    public static SubtitleFrame Assemble(IReadOnlyList<OcrTextLine> lines, int width, int height, string alignment, double? centerX = null, EdgeInk? ink = null, string variant = "Raw")
    {
        var kept = new List<OcrTextLine>(); var rejected = new List<OcrTextLine>();
        var center = centerX ?? width / 2.0;
        var centered = alignment.Equals("Center", StringComparison.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var line = centered ? CenterCluster(raw, center) : raw;
            if (line is null || line.Text.Count(char.IsLetter) < 2 || SubtitleUiFilter.IsControlText(line.Text)) { rejected.Add(raw); continue; }
            if (centered)
            {
                var mid = line.Box.X + line.Box.Width / 2;
                // Centred subtitles: midpoint within 9% of screen width of the screen centre.
                if (Math.Abs(mid - center) > Math.Max(40, width * .09) && line.Box.Width < width * .5) { rejected.Add(raw); continue; }
            }
            kept.Add(line);
        }
        kept.Sort((a, b) => a.Box.Y.CompareTo(b.Box.Y));
        var block = BottomBlock(kept);
        foreach (var line in kept.Except(block)) rejected.Add(line);
        var text = Join(block);
        bool top = false, bottom = false, side = false;
        if (block.Count > 0)
        {
            var left = block.Min(l => l.Box.Left); var right = block.Max(l => l.Box.Right);
            var lineHeight = block.Average(l => l.Box.Height);
            side = left <= 2 || right >= width - 2;
            bottom = block[^1].Box.Bottom >= height - 1;
            // A row of glyphs cut by the top edge is usually not recognised at all, so check pixels,
            // not OCR boxes: outlined subtitle ink in the first rows above the block's columns.
            var span = (int)Math.Max(0, left - lineHeight * 2);
            top = block[0].Box.Top <= 2 || (ink?.TopInkFraction(span, (int)Math.Min(width, right + lineHeight * 2)) ?? 0) >= .015;
        }
        else if (ink is not null) top = ink.TopInkFraction((int)(width * .2), (int)(width * .8)) >= .03;
        return new(text, block, rejected, top, bottom, side, variant);
    }

    // Splits one OCR line into horizontal clusters separated by wide gaps; keeps the cluster nearest
    // the screen centre (HUD text sometimes shares a baseline with subtitles).
    private static OcrTextLine? CenterCluster(OcrTextLine line, double center)
    {
        if (line.Words.Count < 2) return line;
        var gapLimit = Math.Max(24, line.Box.Height * 2.5);
        var clusters = new List<List<OcrWord>> { new() { line.Words[0] } };
        foreach (var word in line.Words.Skip(1))
        {
            var previous = clusters[^1][^1];
            if (word.Box.Left - previous.Box.Right > gapLimit) clusters.Add(new()); clusters[^1].Add(word);
        }
        if (clusters.Count == 1) return line;
        var best = clusters.OrderBy(c => Math.Abs((c.Min(w => w.Box.Left) + c.Max(w => w.Box.Right)) / 2 - center)).First();
        var box = RectangleF.FromLTRB(best.Min(w => w.Box.Left), best.Min(w => w.Box.Top), best.Max(w => w.Box.Right), best.Max(w => w.Box.Bottom));
        return new(string.Join(" ", best.Select(w => w.Text)), box, best);
    }

    private static List<OcrTextLine> BottomBlock(List<OcrTextLine> sorted)
    {
        if (sorted.Count == 0) return [];
        var blocks = new List<List<OcrTextLine>> { new() { sorted[0] } };
        foreach (var line in sorted.Skip(1))
        {
            var previous = blocks[^1][^1];
            var gap = line.Box.Top - previous.Box.Bottom;
            var limit = Math.Max(previous.Box.Height, line.Box.Height) * 1.25;
            if (gap > limit) blocks.Add(new());
            blocks[^1].Add(line);
        }
        return blocks.Last(b => b.Sum(l => l.Text.Count(char.IsLetter)) >= 2);
    }

    public static string Join(IEnumerable<OcrTextLine> rows)
    {
        var output = "";
        foreach (var row in rows.Select(r => Text.Clean(r.Text)).Where(r => r.Length > 0))
        {
            if (output.EndsWith('-') && output.Length > 1 && char.IsLetter(output[^2]) && char.IsLower(row[0])) output = output[..^1] + row;
            else output = output.Length == 0 ? row : output + " " + row;
        }
        return output;
    }

    /// <summary>Region growth when glyphs touch the crop edges. Reference-coordinate region in, region out.</summary>
    public static CaptureRegion? Grow(CaptureRegion region, int referenceWidth, int referenceHeight, bool top, bool side)
    {
        if (referenceWidth <= 0 || referenceHeight <= 0) return null;
        var grown = region;
        if (top)
        {
            var step = Math.Max(16, (int)Math.Round(referenceHeight * .04));
            var maxHeight = (int)(referenceHeight * .32);
            if (grown.Height < maxHeight && grown.Y > 0)
            {
                var add = Math.Min(step, Math.Min(grown.Y, maxHeight - grown.Height));
                grown = grown with { Y = grown.Y - add, Height = grown.Height + add };
            }
        }
        if (side)
        {
            var step = Math.Max(16, (int)Math.Round(referenceWidth * .04));
            var left = Math.Min(step, grown.X); var right = Math.Min(step, referenceWidth - grown.X - grown.Width);
            if (left > 0 || right > 0) grown = grown with { X = grown.X - left, Width = grown.Width + left + right };
        }
        return grown == region ? null : grown;
    }
}

/// <summary>Outlined light-glyph pixels touching the top rows of a crop (subtitle text cut by the crop edge).</summary>
public sealed class EdgeInk
{
    private readonly byte[] rows; private readonly int width, stride, count;
    private EdgeInk(byte[] rows, int width, int stride, int count) { this.rows = rows; this.width = width; this.stride = stride; this.count = count; }
    public static EdgeInk FromTop(Bitmap bitmap, int rowCount = 4)
    {
        var height = Math.Min(bitmap.Height, rowCount + 4);
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * height]; Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return new(bytes, bitmap.Width, data.Stride, Math.Min(rowCount, height));
        }
        finally { bitmap.UnlockBits(data); }
    }
    private int Luma(int x, int y) { var p = y * stride + x * 4; return (rows[p] + rows[p + 1] + rows[p + 2]) / 3; }
    private int Saturation(int x, int y) { var p = y * stride + x * 4; return Math.Max(rows[p], Math.Max(rows[p + 1], rows[p + 2])) - Math.Min(rows[p], Math.Min(rows[p + 1], rows[p + 2])); }
    /// <summary>Highest per-row fraction (over the first rows) of bright, unsaturated pixels that have a darker outline nearby.</summary>
    public double TopInkFraction(int from, int to)
    {
        from = Math.Clamp(from, 0, width - 1); to = Math.Clamp(to, from + 1, width);
        var totalRows = rows.Length / stride; double best = 0; var hits = 0;
        for (var y = 0; y < count; y++)
        {
            var ink = 0;
            for (var x = from; x < to; x++)
            {
                var v = Luma(x, y); if (v < 175 || Saturation(x, y) > 80) continue;
                var dark = v;
                for (var r = 1; r <= 3; r += 2)
                {
                    dark = Math.Min(dark, Luma(Math.Max(0, x - r), y)); dark = Math.Min(dark, Luma(Math.Min(width - 1, x + r), y));
                    dark = Math.Min(dark, Luma(x, Math.Min(totalRows - 1, y + r)));
                }
                if (v - dark >= 55) ink++;
            }
            var fraction = ink / (double)(to - from);
            if (fraction >= .01) hits++;
            best = Math.Max(best, fraction);
        }
        return hits >= 2 ? best : 0; // one stray row (a horizon line, a light pole) is not clipped text
    }
}
