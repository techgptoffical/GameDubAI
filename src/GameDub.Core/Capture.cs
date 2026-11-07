using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace GameDub.Core;

public record GameWindow(int ProcessId, nint Handle, string Name, string Title)
{
    public override string ToString() => $"{Title} ({Name})";
}
public static class GameWindows
{
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    private delegate bool EnumWindowCallback(nint hwnd, nint state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, nint state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int capacity);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    public static List<GameWindow> List()
    {
        var result = new List<GameWindow>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            GetWindowThreadProcessId(handle, out var pid);
            if (pid == 0 || pid == Environment.ProcessId) return true;
            if (DwmGetWindowAttribute(handle, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            try
            {
                var window = FromHandle(handle);
                if (window is null) return true;
                var bounds = Bounds(handle);
                if (bounds.Width >= 160 && bounds.Height >= 80) result.Add(window);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return true;
        }, 0);
        return result.OrderByDescending(IsLikelyGame).ThenByDescending(g => g.Handle == GetForegroundWindow())
            .ThenByDescending(g => GetClientRect(g.Handle, out var rect) ? (long)rect.Right * rect.Bottom : 0)
            .ThenBy(g => g.Title).ToList();
    }
    public static GameWindow? FromHandle(nint handle)
    {
        if (!IsWindow(handle)) return null;
        GetWindowThreadProcessId(handle, out var pid);
        if (pid == 0) return null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var title = new StringBuilder(1024); GetWindowText(handle, title, title.Capacity);
            var name = process.ProcessName;
            if (title.Length == 0 && !IsLikelyGame(new((int)pid, handle, name, name))) return null;
            return new((int)pid, handle, name, title.Length > 0 ? title.ToString() : name);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }
    public static bool IsAlive(GameWindow game)
    {
        if (!IsWindow(game.Handle)) return false;
        GetWindowThreadProcessId(game.Handle, out var owner);
        return owner == game.ProcessId;
    }
    public static bool IsLikelyGame(GameWindow game)
    {
        // Process identity, never title alone: Steam can itself have the title of the game.
        var name = game.Name.ToLowerInvariant();
        return name is "gta5" or "gta5_enhanced" or "gta5_be" or "rdr2" or "witcher3" or "cyberpunk2077"
            or "eldenring" or "hogwartslegacy" or "forzahorizon5" or "forzahorizon6" or "acs" or "acorigins" or "acodyssey"
            || name.EndsWith("-win64-shipping", StringComparison.Ordinal);
    }
    public static GameWindow? ChooseSelection(IReadOnlyList<GameWindow> windows, GameWindow? previous)
    {
        if (previous is not null)
        {
            var exact = windows.FirstOrDefault(w => w.Handle == previous.Handle && w.ProcessId == previous.ProcessId);
            if (exact is not null) return exact;
            var replacement = windows.FirstOrDefault(w => w.ProcessId == previous.ProcessId && w.Name == previous.Name);
            if (replacement is not null) return replacement;
        }
        var candidates = windows.Where(IsLikelyGame).ToArray();
        return candidates.Select(w => w.ProcessId).Distinct().Count() == 1 ? candidates.First() : null;
    }
    public static GameWindow? Rebind(GameWindow game)
    {
        if (IsAlive(game)) return game;
        return List().FirstOrDefault(w => w.ProcessId == game.ProcessId && w.Name == game.Name);
    }
    public static Rectangle Bounds(nint hwnd)
    {
        if (!GetClientRect(hwnd, out var rect)) throw new InvalidOperationException("Selected window is no longer available.");
        var point = new Point();
        if (!ClientToScreen(hwnd, ref point)) throw new InvalidOperationException("Could not locate selected game window.");
        return new(point.X, point.Y, rect.Right, rect.Bottom);
    }
}

public sealed class WindowsSubtitleOcr
{
    private readonly OcrEngine engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
        ?? throw new InvalidOperationException("Windows English OCR is unavailable. Install the English OCR language feature in Windows Settings.");
    public async Task<string> RecognizeAsync(Bitmap bitmap, CancellationToken token)
    {
        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(png.ToArray());
            await writer.StoreAsync().AsTask(token);
            writer.DetachStream();
        }
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
        using var software = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore).AsTask(token);
        var result = await engine.RecognizeAsync(software).AsTask(token);
        return string.Join(" ", result.Lines.Select(l => l.Text));
    }
}

public sealed class SubtitleMonitor
{
    public event Action<string>? Status;
    public double LastOcrMs { get; private set; }
    public long OcrFrames { get; private set; }
    public async Task RunAsync(GameWindow game, CaptureRegion region, IDialogueSink router, CancellationToken token)
    {
        var ocr = new WindowsSubtitleOcr();
        var detector = new SubtitleChangeDetector();
        var clock = Stopwatch.StartNew();
        ulong previousHash = 0;
        var previousText = "";
        var suspended = false;
        while (!token.IsCancellationRequested)
        {
            if (!GameWindows.IsAlive(game))
            {
                router.Interrupt();
                var replacement = GameWindows.Rebind(game);
                if (replacement is null) { Status?.Invoke($"Selected window closed: {game.Title} ({game.Name}). Select the running game, not the closed demo/launcher."); return; }
                game = replacement; detector = new(); previousHash = 0; previousText = "";
                Status?.Invoke($"Reattached to replacement game window: {game.Title}");
            }
            if (GameWindows.IsIconic(game.Handle) || GameWindows.GetForegroundWindow() != game.Handle)
            {
                if (!suspended) { router.Interrupt(); detector = new(); previousHash = 0; previousText = ""; suspended = true; Status?.Invoke("Waiting for the selected game to be in the foreground…"); }
                await Task.Delay(500, token); continue;
            }
            if(suspended) Status?.Invoke("Watching the selected game • waiting for English subtitles in the chosen region");
            suspended = false;
            var bounds = GameWindows.Bounds(game.Handle);
            if (region.X < 0 || region.Y < 0 || region.Width < 20 || region.Height < 20 || region.X + region.Width > bounds.Width || region.Y + region.Height > bounds.Height)
            { router.Interrupt(); throw new InvalidOperationException("Subtitle region is outside the current window. Select it again after resizing the game."); }
            using var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(bounds.X + region.X, bounds.Y + region.Y, 0, 0, bitmap.Size);
            var hash = Fingerprint(bitmap);
            if (hash != previousHash)
            {
                var timer = Stopwatch.StartNew();
                previousText = await ocr.RecognizeAsync(bitmap, token);
                LastOcrMs = timer.Elapsed.TotalMilliseconds; OcrFrames++;
                previousHash = hash;
            }
            var observation = detector.Observe(previousText, clock.Elapsed);
            if (observation.Event == SubtitleEvent.Line)
            {
                Status?.Invoke($"Subtitle detected • OCR {LastOcrMs:0} ms");
                router.Submit(Text.Parse(observation.Text));
            }
            else if (observation.Event == SubtitleEvent.Cleared) { router.SubtitleCleared(); Status?.Invoke("Waiting for next subtitle • current Hindi may finish within its time budget"); }
            await Task.Delay(string.IsNullOrWhiteSpace(previousText) ? 200 : 100, token);
        }
    }
    private static ulong Fingerprint(Bitmap bitmap)
    {
        // Coarse luminance/color quantization helps ignore tiny antialias changes; the crop stays in memory.
        ulong hash = 14695981039346656037;
        for (var y = 0; y < bitmap.Height; y += 4)
            for (var x = 0; x < bitmap.Width; x += 4)
            { var pixel = bitmap.GetPixel(x, y); hash = (hash ^ (uint)((pixel.R / 16 << 8) | (pixel.G / 16 << 4) | pixel.B / 16)) * 1099511628211; }
        return hash;
    }
}
