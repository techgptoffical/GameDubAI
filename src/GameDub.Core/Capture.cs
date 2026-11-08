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
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    public static bool EscapeDown()=>(GetAsyncKeyState(0x1B)&0x8000)!=0;
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

public sealed class WindowsSubtitleOcr(string style = "Raw", int requestedScale = 1, string alignment = "Any")
{
    public string LastVariant { get; private set; } = "Raw";
    public SubtitleFrame LastFrame { get; private set; } = SubtitleFrame.Empty;
    private readonly OcrEngine engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
        ?? throw new InvalidOperationException("Windows English OCR is unavailable. Install the English OCR language feature in Windows Settings.");
    public async Task<string> RecognizeAsync(Bitmap bitmap, CancellationToken token) => (await RecognizeFrameAsync(bitmap, token)).Text;
    /// <param name="centerX">Screen centre in crop coordinates (centred subtitles); defaults to the crop centre.</param>
    public async Task<SubtitleFrame> RecognizeFrameAsync(Bitmap bitmap, CancellationToken token, double? centerX = null)
    {
        var scale = requestedScale == 0 ? (bitmap.Height<140?2:1) : Math.Clamp(requestedScale,1,3);
        scale=Math.Max(1,Math.Min(scale,(int)OcrEngine.MaxImageDimension/Math.Max(bitmap.Width,bitmap.Height)));
        var ink = EdgeInk.FromTop(bitmap);
        SubtitleFrame Layout(IReadOnlyList<OcrTextLine> lines,string variant)=>SubtitleLayout.Assemble(lines,bitmap.Width,bitmap.Height,alignment,centerX,ink,variant);
        SubtitleFrame frame;
        if(style.Equals("Bright",StringComparison.OrdinalIgnoreCase))
        { using var bright=SubtitleImages.BrightText(bitmap); using var large=SubtitleImages.Scale(bright,scale); frame=Layout(await RecognizeRawAsync(large,scale,token),"Bright"); }
        else
        {
            using (var scaled=scale==1?null:SubtitleImages.Scale(bitmap,scale)) frame=Layout(await RecognizeRawAsync(scaled??bitmap,scale,token),"Raw");
            if(style.Equals("Auto",StringComparison.OrdinalIgnoreCase))
            {
                using var mask=SubtitleImages.BrightText(bitmap); using var bigMask=SubtitleImages.Scale(mask,scale);
                var filtered=Layout(await RecognizeRawAsync(bigMask,scale,token),"Bright");
                if(filtered.Kept.Count==frame.Kept.Count&&frame.Kept.Count>1)
                {
                    // Same rows found by both passes: keep the cleaner reading of each row.
                    var rows=frame.Kept.Zip(filtered.Kept,(a,b)=>SubtitleImages.TextScore(b.Text)>SubtitleImages.TextScore(a.Text)+1?b:a).ToArray();
                    frame=frame with { Kept=rows,Text=SubtitleLayout.Join(rows) };
                }
                else if(SubtitleImages.TextScore(filtered.Text)>SubtitleImages.TextScore(frame.Text)+3) frame=filtered with { TopClipped=frame.TopClipped||filtered.TopClipped };
            }
        }
        LastVariant=frame.Variant; LastFrame=frame; return frame;
    }
    private async Task<IReadOnlyList<OcrTextLine>> RecognizeRawAsync(Bitmap bitmap,int scale,CancellationToken token)
    {
        // Direct BGRA copy: no PNG encode/decode per frame.
        var rect=new Rectangle(0,0,bitmap.Width,bitmap.Height);
        var data=bitmap.LockBits(rect,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        byte[] pixels;
        try
        {
            pixels=new byte[bitmap.Width*4*bitmap.Height];
            for(var y=0;y<bitmap.Height;y++) Marshal.Copy(data.Scan0+y*data.Stride,pixels,y*bitmap.Width*4,bitmap.Width*4);
        }
        finally { bitmap.UnlockBits(data); }
        using var writer=new DataWriter(); writer.WriteBytes(pixels);
        using var software=SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(),BitmapPixelFormat.Bgra8,bitmap.Width,bitmap.Height,BitmapAlphaMode.Ignore);
        var result = await engine.RecognizeAsync(software).AsTask(token);
        var output=new List<OcrTextLine>();
        foreach(var line in result.Lines)
        {
            var words=line.Words.Select(w=>new OcrWord(w.Text,new RectangleF((float)w.BoundingRect.X/scale,(float)w.BoundingRect.Y/scale,(float)w.BoundingRect.Width/scale,(float)w.BoundingRect.Height/scale))).ToArray();
            if(words.Length==0)continue;
            var box=RectangleF.FromLTRB(words.Min(w=>w.Box.Left),words.Min(w=>w.Box.Top),words.Max(w=>w.Box.Right),words.Max(w=>w.Box.Bottom));
            output.Add(new(line.Text,box,words));
        }
        return output;
    }
}

public sealed class SubtitleMonitor
{
    public PipelineTrace? Trace { get; set; }
    public event Action<string>? Status;
    public double LastOcrMs { get; private set; }
    public long OcrFrames { get; private set; }
    /// <summary>Raised with the enlarged region (reference coordinates) after clipped subtitles were detected.</summary>
    public event Action<CaptureRegion>? RegionGrown;
    public async Task RunAsync(GameWindow game, CaptureRegion region, IDialogueSink router, CancellationToken token, GameProfile? captureProfile = null)
    {
        var originalRegion=region;
        var ocr = new WindowsSubtitleOcr(captureProfile?.OcrStyle ?? "Raw",captureProfile?.OcrScale ?? 1,captureProfile?.SubtitleAlignment ?? "Any");
        var clippedFrames=0;var grownTimes=0;
        var detector = new SubtitleChangeDetector();
        var clock = Stopwatch.StartNew();
        ulong previousHash = 0;
        var previousText = "";
        var suspended = false;
        var escapeWasDown=false;
        var suppressUntil=TimeSpan.Zero;
        var lastEmitted="";var pauseHint=false;string? lastEventId=null;
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
                if (!suspended) { if(router is IPausableDialogueSink p)p.Pause("Game lost foreground");else router.Interrupt(); detector = new(); previousHash = 0; previousText = ""; suspended = true; Status?.Invoke("Waiting for the selected game • prepared dialogue retained where supported"); }
                await Task.Delay(500, token); continue;
            }
            if(suspended) { if(router is IPausableDialogueSink p)p.Resume();Status?.Invoke("Watching the selected game • waiting for English subtitles in the chosen region"); }
            suspended = false;
            var escapeDown=GameWindows.EscapeDown();
            if(escapeDown && !escapeWasDown)
            {
                if(router is IPausableDialogueSink p) { p.Pause("Game pause input");pauseHint=true; }else router.Interrupt();
                detector=new(); previousText=""; previousHash=0;
                suppressUntil=clock.Elapsed+TimeSpan.FromMilliseconds(250);
                Trace?.Write("pause_hint",null,new { reason="game_escape",identityOfSceneChangeUnknown=true });
                Status?.Invoke("Speech paused • next subtitle resumes. Use Clear scene when skipping a cutscene.");
            }
            escapeWasDown=escapeDown;
            if(clock.Elapsed<suppressUntil) { await Task.Delay(100,token); continue; }
            var bounds = GameWindows.Bounds(game.Handle);
            region=SubtitleImages.Resolve(originalRegion,bounds.Width,bounds.Height,captureProfile);
            if (region.X < 0 || region.Y < 0 || region.Width < 20 || region.Height < 20 || region.X + region.Width > bounds.Width || region.Y + region.Height > bounds.Height)
            { router.Interrupt(); throw new InvalidOperationException("Subtitle region is outside the current window. Select it again after resizing the game."); }
            using var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
            var capturedAt = DateTimeOffset.UtcNow;
            using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(bounds.X + region.X, bounds.Y + region.Y, 0, 0, bitmap.Size);
            var hash = Fingerprint(bitmap);
            var fresh=hash!=previousHash;
            if (fresh)
            {
                var timer = Stopwatch.StartNew();
                var frame = await ocr.RecognizeFrameAsync(bitmap, token, bounds.Width/2.0-region.X);
                previousText = frame.Text;
                LastOcrMs = timer.Elapsed.TotalMilliseconds; OcrFrames++;
                previousHash = hash;
                // Glyphs cut by the crop edge in consecutive distinct frames: grow the saved region
                // (reference coordinates) instead of sending half a subtitle to the translator.
                clippedFrames=frame.TopClipped||frame.SideClipped?clippedFrames+1:0;
                if(clippedFrames>=2&&grownTimes<6)
                {
                    var grown=SubtitleLayout.Grow(originalRegion,captureProfile is { RegionReferenceWidth: >0 }?captureProfile.RegionReferenceWidth:bounds.Width,
                        captureProfile is { RegionReferenceHeight: >0 }?captureProfile.RegionReferenceHeight:bounds.Height,frame.TopClipped,frame.SideClipped);
                    if(grown is not null)
                    {
                        Trace?.Write("region_grown",null,new { from=originalRegion,to=grown,top=frame.TopClipped,side=frame.SideClipped,text=frame.Text });
                        originalRegion=grown;grownTimes++;clippedFrames=0;detector=new();previousHash=0;previousText="";
                        RegionGrown?.Invoke(grown);
                        Status?.Invoke("Subtitle text was cut by the capture edge • region enlarged automatically");
                        await Task.Delay(30,token);continue;
                    }
                }
            }
            var observation = detector.Observe(previousText, clock.Elapsed);
            if(fresh || observation.Event!=SubtitleEvent.None)
                Trace?.Write("ocr", null, new { text = previousText, confidence = (double?)null, confidenceAvailable = false, ocrMs = LastOcrMs,
                capturedAt, detectorEvent = observation.Event.ToString(), variant = ocr.LastVariant, region, clientWidth = bounds.Width, clientHeight = bounds.Height,
                rows = ocr.LastFrame.Kept.Count, rejected = ocr.LastFrame.Rejected.Select(l=>l.Text).ToArray(), topClipped = ocr.LastFrame.TopClipped, sideClipped = ocr.LastFrame.SideClipped });
            Trace?.Frame(bitmap,previousText,LastOcrMs,ocr.LastFrame);
            if (observation.Event == SubtitleEvent.Line)
            {
                if(pauseHint && router is IPausableDialogueSink p)p.Resume();pauseHint=false;
                if(SubtitleChangeDetector.Similar(observation.Text,lastEmitted,.85)) { await Task.Delay(100,token);continue; }
                lastEmitted=observation.Text;
                Status?.Invoke($"Subtitle detected • OCR {LastOcrMs:0} ms");
                var line = Text.Parse(observation.Text) with { FirstSeenAt = capturedAt-(clock.Elapsed-observation.FirstSeen), Supersedes = observation.Extends ? lastEventId : null };
                lastEventId=line.EventId;
                Trace?.Write("detected",line,new { english = line.English, firstObservedAt = line.FirstSeenAt, stabilizationMs = (clock.Elapsed-observation.FirstSeen).TotalMilliseconds });
                router.Submit(line);
            }
            else if (observation.Event == SubtitleEvent.Cleared) { if(!pauseHint)lastEmitted="";router.SubtitleCleared(); Status?.Invoke("Waiting for next subtitle • dialogue retained"); }
            await Task.Delay(100, token);
        }
    }
    private static ulong Fingerprint(Bitmap bitmap)
    {
        // Coarse luminance/color quantization helps ignore tiny antialias changes; the crop stays in memory.
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            ulong hash = 14695981039346656037;
            var row = new byte[data.Stride];
            for (var y = 0; y < bitmap.Height; y += 4)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (var x = 0; x < bitmap.Width; x += 4)
                { var p = x * 4; hash = (hash ^ (uint)((row[p + 2] / 16 << 8) | (row[p + 1] / 16 << 4) | row[p] / 16)) * 1099511628211; }
            }
            return hash;
        }
        finally { bitmap.UnlockBits(data); }
    }
}
