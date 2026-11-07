using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace GameDub.Core;

// Windows ApplicationLoopback API, adapted to NAudio's MIT-licensed COM wrappers.
// Captures ONLY the selected PID tree, never the microphone or the system-wide mix.
public sealed class ProcessAudioCapture
{
    public event Action<byte[]>? DataAvailable;
    [StructLayout(LayoutKind.Sequential)] private struct Activation { public int Type; public uint Pid; public int Mode; }
    [StructLayout(LayoutKind.Sequential)] private struct BlobVariant
    { public ushort Type, Reserved1, Reserved2, Reserved3; public uint Size; public nint Data; }
    [ComVisible(true), Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICompletion { [PreserveSig] int ActivateCompleted(IActivationOperation operation); }
    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivationOperation { [PreserveSig] int GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object activated); }
    [ComVisible(true), Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] public interface IAgile { }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Completion : ICompletion, IAgile
    {
        public readonly TaskCompletionSource<IAudioClient> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ActivateCompleted(IActivationOperation operation)
        {
            try
            {
                Marshal.ThrowExceptionForHR(operation.GetActivateResult(out var hr, out var activated)); Marshal.ThrowExceptionForHR(hr);
                Result.TrySetResult((IAudioClient)activated);
            }
            catch (Exception error) { Result.TrySetException(error); }
            return 0;
        }
    }
    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(string device, [In] ref Guid iid, nint parameters, nint callback, out IActivationOperation operation);

    private static async Task<AudioClient> ActivateAsync(uint pid)
    {
        if (pid == 0 || pid == Environment.ProcessId) throw new InvalidOperationException("Select an external game process to avoid capturing generated Hindi.");
        var data = Marshal.AllocHGlobal(Marshal.SizeOf<Activation>());
        var variant = Marshal.AllocHGlobal(Marshal.SizeOf<BlobVariant>());
        var completion = new Completion();
        var handler = Marshal.GetComInterfaceForObject(completion, typeof(ICompletion));
        IActivationOperation? operation = null;
        try
        {
            Marshal.StructureToPtr(new Activation { Type = 1, Pid = pid, Mode = 0 }, data, false);
            Marshal.StructureToPtr(new BlobVariant { Type = 65, Size = (uint)Marshal.SizeOf<Activation>(), Data = data }, variant, false);
            var iid = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(@"VAD\Process_Loopback", ref iid, variant, handler, out operation));
            var client = await completion.Result.Task.ConfigureAwait(false);
            return new AudioClient(client);
        }
        finally
        {
            GC.KeepAlive(completion); Marshal.Release(handler);
            if (operation is not null && Marshal.IsComObject(operation)) Marshal.ReleaseComObject(operation);
            Marshal.FreeHGlobal(variant); Marshal.FreeHGlobal(data);
        }
    }
    public async Task RunAsync(int processId, CancellationToken token)
    {
        using var client = await ActivateAsync((uint)processId);
        using var signal = new AutoResetEvent(false);
        var format = new WaveFormat(16000, 16, 1);
        client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality, 0, 0, format, Guid.Empty);
        client.SetEventHandle(signal.SafeWaitHandle.DangerousGetHandle());
        var capture = client.AudioCaptureClient;
        token.ThrowIfCancellationRequested(); client.Start();
        try
        {
            await Task.Run(() =>
            {
                var waits = new[] { signal, token.WaitHandle };
                while (!token.IsCancellationRequested)
                {
                    if (WaitHandle.WaitAny(waits, 200) == 1) break;
                    while (capture.GetNextPacketSize() > 0)
                    {
                        var pointer = capture.GetBuffer(out var frames, out var flags);
                        try
                        {
                            var bytes = new byte[frames * format.BlockAlign];
                            if (!flags.HasFlag(AudioClientBufferFlags.Silent)) Marshal.Copy(pointer, bytes, 0, bytes.Length);
                            DataAvailable?.Invoke(bytes);
                        }
                        finally { capture.ReleaseBuffer(frames); }
                    }
                }
            }, token);
        }
        finally { client.Stop(); }
    }
}
