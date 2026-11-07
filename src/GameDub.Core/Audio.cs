using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GameDub.Core;

public record AudioDevice(string Id, string Name) { public override string ToString() => Name; }
public sealed class WasapiPlayer : IAudioPlayer, IDisposable
{
    private readonly object gate = new();
    private WasapiOut? active;
    public string DeviceId { get; set; } = "";
    public float Volume { get; set; } = 0.7f;
    public static List<AudioDevice> Devices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<AudioDevice> { new("", "Windows default output") };
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            using (device) devices.Add(new(device.ID, device.FriendlyName));
        return devices;
    }
    public static WaveStream Decode(AudioClip clip)
    {
        var memory = new MemoryStream(clip.Bytes, false);
        try
        {
            if (clip.Format == "wav") return new WaveFileReader(memory);
            if (clip.Format.StartsWith("pcm_")) return new RawSourceWaveStream(memory, new WaveFormat(int.Parse(clip.Format[4..]), 16, 1));
            throw new InvalidDataException("Unsupported audio format.");
        }
        catch { memory.Dispose(); throw; }
    }
    public async Task PlayAsync(AudioClip clip, Action started, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        using var device = string.IsNullOrEmpty(DeviceId) ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(DeviceId);
        using var source = Decode(clip);
        using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 60);
        var provider = new VolumeSampleProvider(source.ToSampleProvider()) { Volume = Math.Clamp(Volume, 0, 1) };
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, e) => { if (e.Exception is not null) done.TrySetException(new IOException("Audio output stopped; check the selected device.", e.Exception)); else done.TrySetResult(); };
        output.Init(provider); // WASAPI shared mode converts to the endpoint's sample rate/channels.
        lock (gate) { token.ThrowIfCancellationRequested(); active = output; }
        using var registration = token.Register(() => { lock (gate) if (active == output) output.Stop(); });
        try
        {
            token.ThrowIfCancellationRequested();
            output.Play();
            started(); // Playback request, not a measured acoustic arrival time.
            await done.Task.WaitAsync(token);
        }
        finally { lock (gate) { output.Stop(); if (active == output) active = null; } }
    }
    public void Stop() { lock (gate) active?.Stop(); }
    public void Dispose() => Stop();
    public static void SaveWave(AudioClip clip, string path)
    {
        using var reader = Decode(clip);
        WaveFileWriter.CreateWaveFile(path, reader);
    }
}
