using System.Diagnostics;

namespace GameDub.Core;

/// <summary>
/// The game owns the GPU. GameDub's GPU models (Gemma ~2.5 GB, IndicF5 ~1.8 GB) may load only when enough
/// video memory stays free for the game, and are shed while playing if free memory runs low.
/// Cause: GTA V Enhanced (DX12, ray tracing) crashed in the NVIDIA driver when IndicF5 loaded mid-game.
/// </summary>
public static class GpuGuard
{
    public const int LlmMiB = 2600, NaturalVoiceMiB = 1800;
    /// <summary>Free VRAM that must remain after a GameDub model loads.</summary>
    public static int LoadReserveMiB { get; set; } = 2048;
    /// <summary>Below this much free VRAM during play, GameDub releases its GPU models.</summary>
    public static int ShedBelowMiB { get; set; } = 900;
    public static volatile bool VoiceShed, LlmShed;
    public static string? ShedReason { get; private set; }
    private static readonly object gate = new();
    private static (DateTime At, (int Used, int Total)? Value) cached;

    public static (int UsedMiB, int TotalMiB)? Query()
    {
        lock (gate)
        {
            if (DateTime.UtcNow - cached.At < TimeSpan.FromMilliseconds(800)) return cached.Value;
            (int, int)? value = null;
            try
            {
                using var p = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used,memory.total --format=csv,noheader,nounits")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
                var line = p.StandardOutput.ReadLine() ?? ""; p.WaitForExit(3000);
                var parts = line.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], out var used) && int.TryParse(parts[1], out var total)) value = (used, total);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
            cached = (DateTime.UtcNow, value);
            return value;
        }
    }
    public static int? FreeMiB => Query() is { } q ? q.TotalMiB - q.UsedMiB : null;

    /// <summary>True when loading needMiB still leaves the reserve free. Unknown GPU state → allowed (non-NVIDIA).</summary>
    public static bool CanLoad(int needMiB, out string reason)
    {
        var free = FreeMiB;
        if (free is null) { reason = "GPU memory unknown"; return true; }
        if (free.Value - needMiB >= LoadReserveMiB) { reason = $"{free} MiB free"; return true; }
        reason = $"only {free} MiB video memory free; the game needs it (GameDub model needs {needMiB} MiB + {LoadReserveMiB} MiB reserve)";
        return false;
    }

    public static void Shed(bool voice, bool llm, string reason)
    {
        if (voice) VoiceShed = true;
        if (llm) LlmShed = true;
        ShedReason = reason;
    }
    public static void Reset() { VoiceShed = LlmShed = false; ShedReason = null; }
}

/// <summary>Polls free VRAM while GameDub runs; releases the voice model first, then the LLM.</summary>
public sealed class GpuWatch : IDisposable
{
    private readonly System.Threading.Timer timer;
    private readonly Action<string> releaseVoice, releaseLlm;
    private int busy;
    public GpuWatch(Action<string> releaseVoice, Action<string> releaseLlm, int periodMs = 2500)
    {
        this.releaseVoice = releaseVoice; this.releaseLlm = releaseLlm;
        timer = new System.Threading.Timer(_ => Tick(), null, periodMs, periodMs);
    }
    private void Tick()
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        try
        {
            if (GpuGuard.FreeMiB is not { } free || free >= GpuGuard.ShedBelowMiB) return;
            var reason = $"Only {free} MiB video memory left for the game";
            if (!GpuGuard.VoiceShed) { GpuGuard.Shed(true, false, reason); releaseVoice(reason); }
            else if (!GpuGuard.LlmShed && free < GpuGuard.ShedBelowMiB / 2) { GpuGuard.Shed(false, true, reason); releaseLlm(reason); }
        }
        finally { Volatile.Write(ref busy, 0); }
    }
    public void Dispose() => timer.Dispose();
}
