using NAudio.CoreAudioApi;

namespace GameDub.Core;

public sealed class GameDuckingPlayer(IAudioPlayer inner) : IAudioPlayer,IPausableAudioPlayer
{
    public int ProcessId { get; set; }
    public bool Enabled { get; set; }
    public float Level { get; set; } = .3f;
    private readonly object gate=new();
    private DuckLease? activeDuck;
    private bool playing;
    public async Task PlayAsync(AudioClip clip, Action started, CancellationToken token)
    {
        lock(gate) { playing=true;activeDuck=Enabled&&ProcessId>0?new DuckLease(ProcessId,Level):null; }
        try { await inner.PlayAsync(clip, started, token); }
        finally { lock(gate) { playing=false;activeDuck?.Dispose();activeDuck=null; } }
    }
    public void Stop() { inner.Stop();lock(gate) { activeDuck?.Dispose();activeDuck=null; } }
    public void Pause() { if(inner is IPausableAudioPlayer p)p.Pause();lock(gate) { activeDuck?.Dispose();activeDuck=null; } }
    public void Resume() { lock(gate)if(playing&&activeDuck is null&&Enabled&&ProcessId>0)activeDuck=new(ProcessId,Level);if(inner is IPausableAudioPlayer p)p.Resume(); }
    private sealed class DuckLease : IDisposable
    {
        private readonly List<(AudioSessionControl Session, float Original, float Applied)> sessions = new();
        private readonly List<MMDevice> devices = new();
        private readonly MMDeviceEnumerator enumerator = new();
        public DuckLease(int pid, float factor)
        {
            try
            {
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    devices.Add(device);
                    SessionCollection collection;
                    try { collection = device.AudioSessionManager.Sessions; }
                    catch (System.Runtime.InteropServices.COMException) { continue; } // Some active virtual/HDMI endpoints do not expose sessions.
                    for (var i = 0; i < collection.Count; i++)
                    {
                        var session = collection[i];
                        if (session.GetProcessID != (uint)pid) { session.Dispose(); continue; }
                        var original = session.SimpleAudioVolume.Volume;
                        var applied = original * Math.Clamp(factor, 0, 1);
                        sessions.Add((session, original, applied));
                        session.SimpleAudioVolume.Volume = applied;
                    }
                }
            }
            catch (Exception error) { Dispose(); throw new IOException("Could not duck the selected game's audio. Disable ducking and retry.", error); }
        }
        public void Dispose()
        {
            foreach (var item in sessions)
            {
                try
                {
                    // Preserve an intentional user volume change during playback.
                    if (Math.Abs(item.Session.SimpleAudioVolume.Volume - item.Applied) < .002f) item.Session.SimpleAudioVolume.Volume = item.Original;
                }
                catch (System.Runtime.InteropServices.COMException) { }
                finally { item.Session.Dispose(); }
            }
            sessions.Clear();
            foreach (var device in devices) device.Dispose();
            devices.Clear(); enumerator.Dispose();
        }
    }
}
