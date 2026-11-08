using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameDub.Core;

/// <summary>
/// The game owns the PC. GameDub's models may load only when enough video memory AND system memory (commit)
/// stays free for the game, and are shed while playing if either runs low.
/// Causes: GTA V Enhanced crashed in the NVIDIA driver when IndicF5 loaded mid-game (video memory), and on a
/// 16 GB PC the system commit limit was exhausted (dwm, GTA and the translator all failed to allocate).
/// Measured commit per model (GPU allocations are charged to system commit too): Gemma server ~3.6 GB,
/// IndicF5 ~4.1 GB, Marian ~0.9 GB, Kokoro CPU ~0.6 GB.
/// </summary>
public static class GpuGuard
{
    public const int LlmMiB = 2600, NaturalVoiceMiB = 1800;
    public const int LlmCommitMiB = 3700, NaturalVoiceCommitMiB = 4200;
    public const int KokoroGpuMiB = 700, KokoroGpuCommitMiB = 1300;
    /// <summary>Free VRAM that must remain after a GameDub model loads.</summary>
    public static int LoadReserveMiB { get; set; } = 2048;
    /// <summary>Below this much free VRAM during play, GameDub releases its GPU models.</summary>
    public static int ShedBelowMiB { get; set; } = 900;
    /// <summary>System commit that must remain after a model loads while the game is running.</summary>
    public static int CommitReserveMiB { get; set; } = 3072;
    /// <summary>System commit kept free for a game that has not started yet (GTA V Enhanced needs several GB to launch).</summary>
    public static int GameStartReserveMiB { get; set; } = 9216;
    /// <summary>Below this much available commit (or physical memory) during play, GameDub releases models.</summary>
    public static int ShedCommitBelowMiB { get; set; } = 1536;
    public static int ShedPhysicalBelowMiB { get; set; } = 400;
    public static volatile bool VoiceShed, LlmShed;
    public static string? ShedReason { get; private set; }
    /// <summary>True when the game is already running (its memory is already counted). Set by the app.</summary>
    public static Func<bool> GameRunning { get; set; } = KnownGameRunning;
    public static Func<(int UsedMiB, int TotalMiB)?> VramProbe { get; set; } = QueryVram;
    public static Func<MemoryState?> MemoryProbe { get; set; } = QueryMemory;
    private static readonly object gate = new();
    private static (DateTime At, (int Used, int Total)? Value) cached;
    private static readonly string[] heavyGames = ["GTA5_Enhanced", "GTA5", "PlayGTAV"];

    public readonly record struct MemoryState(long AvailableCommitMiB, long CommitLimitMiB, long AvailablePhysicalMiB);

    public static bool KnownGameRunning()
    {
        foreach (var name in heavyGames)
        {
            var found = Process.GetProcessesByName(name);
            var any = found.Length > 0; foreach (var p in found) p.Dispose();
            if (any) return true;
        }
        return false;
    }

    public static (int UsedMiB, int TotalMiB)? Query() => VramProbe();
    private static (int UsedMiB, int TotalMiB)? QueryVram()
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

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);
    /// <summary>ullTotalPageFile/ullAvailPageFile are the system commit limit / available commit.</summary>
    private static MemoryState? QueryMemory()
    {
        var s = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref s)) return null;
        return new((long)(s.ullAvailPageFile >> 20), (long)(s.ullTotalPageFile >> 20), (long)(s.ullAvailPhys >> 20));
    }

    /// <summary>
    /// True when loading a model (needVramMiB on the GPU, needCommitMiB of system commit) still leaves the game its reserves.
    /// Unknown GPU state → only the system memory check applies (non-NVIDIA).
    /// </summary>
    public static bool CanLoad(int needVramMiB, int needCommitMiB, out string reason)
    {
        var free = FreeMiB;
        if (free is { } f && f - needVramMiB < LoadReserveMiB)
        {
            reason = $"only {f} MiB video memory free; the game needs it (model needs {needVramMiB} MiB + {LoadReserveMiB} MiB reserve)";
            return false;
        }
        if (MemoryProbe() is { } m)
        {
            var running = GameRunning();
            var reserve = running ? CommitReserveMiB : Math.Max(CommitReserveMiB, GameStartReserveMiB);
            if (m.AvailableCommitMiB - needCommitMiB < reserve)
            {
                reason = $"only {m.AvailableCommitMiB / 1024.0:0.0} GB system memory free; model needs {needCommitMiB / 1024.0:0.0} GB and " +
                         (running ? $"the game keeps {reserve / 1024.0:0.0} GB" : $"{reserve / 1024.0:0.0} GB stays free for the game to start");
                return false;
            }
            reason = $"{free?.ToString() ?? "?"} MiB video memory, {m.AvailableCommitMiB / 1024.0:0.0} GB system memory free";
            return true;
        }
        reason = free is null ? "memory state unknown" : $"{free} MiB free";
        return true;
    }

    /// <summary>Clears an earlier shed when everything that was released fits again now.</summary>
    public static bool TryRestore(out string reason)
    {
        reason = "";
        if (!VoiceShed && !LlmShed) return false;
        var vram = (VoiceShed ? NaturalVoiceMiB : 0) + (LlmShed ? LlmMiB : 0);
        var commit = (VoiceShed ? NaturalVoiceCommitMiB : 0) + (LlmShed ? LlmCommitMiB : 0);
        if (!CanLoad(vram, commit, out reason)) return false;
        Reset(); return true;
    }

    public static void Shed(bool voice, bool llm, string reason)
    {
        if (voice) VoiceShed = true;
        if (llm) LlmShed = true;
        ShedReason = reason;
    }
    public static void Reset() { VoiceShed = LlmShed = false; ShedReason = null; }
}

/// <summary>Polls free VRAM and system memory while GameDub runs; releases the voice model first, then the LLM.</summary>
public sealed class GpuWatch : IDisposable
{
    private readonly System.Threading.Timer? timer;
    private readonly Action<string> releaseVoice, releaseLlm;
    private int busy;
    public GpuWatch(Action<string> releaseVoice, Action<string> releaseLlm, int periodMs = 2500)
    {
        this.releaseVoice = releaseVoice; this.releaseLlm = releaseLlm;
        if (periodMs > 0) timer = new System.Threading.Timer(_ => Check(), null, periodMs, periodMs);
    }
    public void Check()
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        try
        {
            string? reason = null; var critical = false;
            if (GpuGuard.FreeMiB is { } free && free < GpuGuard.ShedBelowMiB)
            { reason = $"Only {free} MiB video memory left for the game"; critical = free < GpuGuard.ShedBelowMiB / 2; }
            if (GpuGuard.MemoryProbe() is { } m && (m.AvailableCommitMiB < GpuGuard.ShedCommitBelowMiB || m.AvailablePhysicalMiB < GpuGuard.ShedPhysicalBelowMiB))
            {
                // Releasing the voice frees ~4 GB at once; if memory is still short on the next check, the LLM goes too.
                reason = $"Only {m.AvailableCommitMiB / 1024.0:0.0} GB system memory left for the game"; critical = true;
            }
            if (reason is null) return;
            if (!GpuGuard.VoiceShed) { GpuGuard.Shed(true, false, reason); releaseVoice(reason); }
            else if (!GpuGuard.LlmShed && critical) { GpuGuard.Shed(false, true, reason); releaseLlm(reason); }
        }
        finally { Volatile.Write(ref busy, 0); }
    }
    public void Dispose() => timer?.Dispose();
}
