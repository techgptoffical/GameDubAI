using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameDub.Core;

/// <summary>
/// Kill-on-close Windows job: model workers (llama.cpp, Python) die with GameDub even after a crash or
/// Task Manager kill, so no orphaned process keeps gigabytes of RAM/VRAM while a game runs.
/// </summary>
public static class ChildProcessJob
{
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimit { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public nuint MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong a, b, c, d, e, f; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit { public BasicLimit Basic; public IoCounters Io; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimit info, int length);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(nint job, nint process);
    private static readonly Lazy<nint> Job = new(() =>
    {
        var job = CreateJobObject(0, null);
        if (job == 0) return 0;
        var info = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = 0x2000 } }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        return SetInformationJobObject(job, 9, ref info, Marshal.SizeOf<ExtendedLimit>()) ? job : 0;
    });
    public static void Attach(Process process)
    {
        try { if (Job.Value != 0) AssignProcessToJobObject(Job.Value, process.Handle); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
