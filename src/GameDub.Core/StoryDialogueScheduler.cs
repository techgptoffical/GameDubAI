using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace GameDub.Core;

public interface IDialogueController : IDialogueSink,IAsyncDisposable
{
    event Action<string>? Error;
    event Action<string>? Status;
    long DroppedCount { get; }
    int PendingCount { get; }
}
public interface IPausableDialogueSink
{
    void Pause(string reason);
    void Resume();
}
public enum DialoguePolicy { StoryComplete, LiveSync }

public sealed class StoryDialogueScheduler : IDialogueController,IPausableDialogueSink
{
    private sealed class Prepared
    {
        public readonly Channel<AudioClip> Chunks=Channel.CreateBounded<AudioClip>(new BoundedChannelOptions(1) { SingleReader=true,SingleWriter=true });
        public volatile bool Discarded;
    }
    private readonly DubbingPipeline pipeline;
    private readonly DialogueEventStore store;
    private readonly DialoguePolicy policy;
    private readonly object gate=new();
    private readonly CancellationTokenSource lifetime=new();
    private CancellationTokenSource sceneCancellation=new();
    private readonly ConcurrentDictionary<string,Prepared> prepared=new();
    private readonly SemaphoreSlim pulse=new(0,1);
    private readonly Task[] workers;
    private readonly TranslationTextCache translations;
    private volatile bool paused,needsCatchup,catchingUp;
    private int scene;
    private int disposed;
    private string? lastSubtitle;
    private long dropped;
    private readonly int liveDeadlineMs;
    public event Action<string>? Error;
    public event Action<string>? Status;
    public long DroppedCount=>Interlocked.Read(ref dropped);
    public int PendingCount=>(int)Math.Min(int.MaxValue,store.Count(scene,"playback IN ('Pending','Ready','Deferred')"));
    public int PreparedCount=>prepared.Count;
    public bool NeedsCatchup=>needsCatchup;
    public DialogueEventStore Events=>store;
    public StoryDialogueScheduler(DubbingPipeline pipeline,DialogueEventStore store,DialoguePolicy policy=DialoguePolicy.StoryComplete,int liveDeadlineMs=3500,string? translationCacheDirectory=null)
    {
        this.pipeline=pipeline;this.store=store;this.policy=policy;this.liveDeadlineMs=liveDeadlineMs;
        translations=new(translationCacheDirectory??Path.Combine(Paths.Root,"translation-cache"));
        workers=[Task.Run(TranslateLoop),Task.Run(SynthesizeLoop),Task.Run(PlaybackLoop)];
    }
    private void Wake() { try { pulse.Release(); } catch(SemaphoreFullException) { } catch(ObjectDisposedException) { } }
    private Task Tick()=>pulse.WaitAsync(80,lifetime.Token);
    public void Submit(Dialogue line)
    {
        if(Volatile.Read(ref disposed)!=0)return;
        lock(gate)
        {
            if(disposed!=0)return;
            if(store.Get(line.EventId) is not null) { pipeline.Trace?.Write("duplicate_event",line,new { reason="event_id_already_recorded" });return; }
            if(lastSubtitle is not null)store.Update(lastSubtitle,e=>e.DisappearedAt??=DateTimeOffset.UtcNow);
            var voice=CharacterVoices.Assign(line,pipeline.SchedulerProfile);
            var item=store.Add(line,scene,voice);lastSubtitle=item.Id;
            pipeline.Trace?.Write("captured_event",line,new { sequence=item.Sequence,speaker=item.Speaker,voice=item.VoiceId,evidence=item.SpeakerEvidence,confidence=item.SpeakerConfidence,policy=policy.ToString() });
        }
        Wake();
    }
    public void SubtitleCleared()
    {
        if(Volatile.Read(ref disposed)!=0)return;
        lock(gate)if(lastSubtitle is not null) { store.Update(lastSubtitle,e=>e.DisappearedAt??=DateTimeOffset.UtcNow);lastSubtitle=null; }
    }
    public void Pause(string reason)
    {
        if(Volatile.Read(ref disposed)!=0)return;
        paused=true;if(!catchingUp && pipeline.SchedulerPlayer is IPausableAudioPlayer player)player.Pause();
        pipeline.Trace?.Write("paused",null,new { reason,retained=true });
    }
    public void Resume()
    {
        if(Volatile.Read(ref disposed)!=0)return;
        paused=false;if(pipeline.SchedulerPlayer is IPausableAudioPlayer player)player.Resume();Wake();
    }
    public void CatchUp()
    {
        if(Volatile.Read(ref disposed)!=0)return;
        catchingUp=true;needsCatchup=false;if(pipeline.SchedulerPlayer is IPausableAudioPlayer p)p.Resume();
        Status?.Invoke("Catching up remembered dialogue in order. Pause the game if you want to hear every line.");Wake();
    }
    public void Interrupt()=>SkipScene("Explicit scene skip / stop");
    public void SkipScene(string reason)
    {
        lock(gate) { if(disposed!=0)return;SkipSceneCore(reason); }
    }
    private void SkipSceneCore(string reason)
    {
        lock(gate)
        {
            sceneCancellation.Cancel();store.CancelScene(scene,reason);scene++;
            sceneCancellation.Dispose();sceneCancellation=new();lastSubtitle=null;needsCatchup=false;catchingUp=false;
            pipeline.SchedulerPlayer.Stop();pipeline.Context.Clear();
            foreach(var pair in prepared) { pair.Value.Discarded=true;pair.Value.Chunks.Writer.TryComplete(new OperationCanceledException(reason)); }
            prepared.Clear();
        }
        pipeline.Trace?.Write("scene_interrupt",null,new { reason,historyRetained=true });Wake();
    }
    private CancellationTokenSource JobToken(int expectedScene)
    {
        lock(gate)
        {
            var token=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,sceneCancellation.Token);
            if(expectedScene!=scene)token.Cancel();return token;
        }
    }
    private bool Current(DialogueEvent e)=>e.Scene==scene&&!lifetime.IsCancellationRequested;
    private void UpdateCurrent(DialogueEvent item,Action<DialogueEvent> edit)
    {
        lock(gate)if(Current(item))store.Update(item.Id,e=> { if(e.PlaybackState!="Cancelled")edit(e); });
    }
    private bool Expired(DialogueEvent e)=>policy==DialoguePolicy.LiveSync&&(DateTimeOffset.UtcNow-e.AppearedAt).TotalMilliseconds>liveDeadlineMs;
    private void Unspoken(DialogueEvent e,string reason)
    {
        if(prepared.TryRemove(e.Id,out var packet)) { packet.Discarded=true;packet.Chunks.Writer.TryComplete(); }
        UpdateCurrent(e,x=> { x.PlaybackState="Unspoken";x.CancellationReason=reason; });Interlocked.Increment(ref dropped);
        pipeline.Trace?.Write("unspoken_history",e.ToDialogue(),new { reason,hindiPreserved=true });
    }
    private async Task TranslateLoop()
    {
        try
        {
            while(!lifetime.IsCancellationRequested)
            {
                var item=store.First(scene,"translation='Pending' AND playback!='Cancelled'");if(item is null) { await Tick();continue; }
                using var job=JobToken(item.Scene);UpdateCurrent(item,e=>e.TranslationState="Translating");var watch=Stopwatch.StartNew();
                try
                {
                    var history=pipeline.Context.Snapshot();
                    var key=TranslationTextCache.Identity(item.ToDialogue(),pipeline.SchedulerProfile,pipeline.SchedulerLocalizer.Identity, pipeline.SchedulerLocalizer is OfflineLocalizer?[]:history);
                    var line=translations.Get(key)??await pipeline.SchedulerLocalizer.TranslateAsync(item.ToDialogue(),pipeline.SchedulerProfile,history,job.Token);
                    job.Token.ThrowIfCancellationRequested();translations.Put(key,line);
                    if(!Current(item))continue;
                    UpdateCurrent(item,e=> { e.Hindi=line.Hindi;e.TranslationSource=line.Source;e.TranslationState="Ready";e.TranslationMs=watch.Elapsed.TotalMilliseconds; });
                    pipeline.Context.Add(item.ToDialogue(),line);pipeline.PublishLocalized(item.ToDialogue(),line);
                    pipeline.Trace?.Write("translated",item.ToDialogue(),new { hindi=line.Hindi,translationMs=watch.Elapsed.TotalMilliseconds,source=line.Source });
                }
                catch(OperationCanceledException)when(job.IsCancellationRequested) { }
                catch(Exception e) { UpdateCurrent(item,x=> { x.TranslationState="Failed";x.PlaybackState="Unspoken";x.Error=e.Message;x.CancellationReason="Translation failed; English retained"; });Error?.Invoke(e.Message); }
                Wake();
            }
        }
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested) { }
    }
    private async Task SynthesizeLoop()
    {
        try
        {
            while(!lifetime.IsCancellationRequested)
            {
                if(prepared.Count>=3 || (needsCatchup&&!catchingUp)) { await Tick();continue; }
                var item=store.First(scene,"translation='Ready' AND synthesis='Pending' AND playback IN ('Pending','Deferred')");
                if(item is null) { await Tick();continue; }
                if(Expired(item)) { Unspoken(item,"Live Sync deadline; translated caption retained");continue; }
                using var job=JobToken(item.Scene);var packet=new Prepared();if(!prepared.TryAdd(item.Id,packet)) { await Tick();continue; }
                var localized=new LocalizedLine(item.Hindi,"neutral",item.TranslationSource);var watch=Stopwatch.StartNew();
                UpdateCurrent(item,e=>e.SynthesisState="Generating");
                try
                {
                    var chunks=pipeline.SchedulerVoice is IChunkedVoiceProvider chunked?chunked.DeliveryChunks(localized):[];
                    if(chunks.Count==0)chunks=[localized];var clips=new List<AudioClip>();double duration=0;long bytes=0;
                    foreach(var chunk in chunks)
                    {
                        if(packet.Discarded)throw new OperationCanceledException();
                        job.Token.ThrowIfCancellationRequested();
                        var clip=await pipeline.SchedulerVoice.SynthesizeAsync(chunk,item.VoiceId,job.Token);
                        job.Token.ThrowIfCancellationRequested();if(packet.Discarded)throw new OperationCanceledException();
                        bytes+=clip.Bytes.Length;
                        if(bytes>8*1024*1024)throw new InvalidDataException("Speech event exceeds the 8 MiB buffer limit; text retained.");
                        using(var decoded=WasapiPlayer.Decode(clip))duration+=decoded.TotalTime.TotalMilliseconds;
                        clips.Add(clip);var first=clips.Count==1;
                        UpdateCurrent(item,e=> { e.ExpectedSpeechMs=duration;e.SynthesisMs=watch.Elapsed.TotalMilliseconds;if(first&&e.PlaybackState!="Unspoken"){ e.FirstChunkMs=watch.Elapsed.TotalMilliseconds;e.ReadyAt=DateTimeOffset.UtcNow;e.PlaybackState="Ready"; } });
                        while(!packet.Chunks.Writer.TryWrite(clip)) { if(packet.Discarded)throw new OperationCanceledException();await Task.Delay(20,job.Token); }
                        Wake();
                    }
                    job.Token.ThrowIfCancellationRequested();
                    var complete=clips[0];
                    if(clips.Count>1)
                    {
                        if(clips.Any(c=>c.Format!=complete.Format)||complete.Format=="wav")throw new InvalidDataException("Cannot combine incompatible delivery chunks.");
                        using var memory=new MemoryStream();foreach(var c in clips)memory.Write(c.Bytes);complete=complete with { Bytes=memory.ToArray() };
                    }
                    var audioKey=TranslationTextCache.Hash(item.Hindi+"|"+item.VoiceId+"|"+pipeline.SchedulerVoice.Identity);
                    pipeline.SchedulerCache.Put(audioKey,localized,complete);
                    UpdateCurrent(item,e=> { e.SynthesisState="Ready";e.AudioCacheKey=audioKey;e.SynthesisMs=watch.Elapsed.TotalMilliseconds; });
                    packet.Chunks.Writer.TryComplete();
                    pipeline.PublishReady(item.ToDialogue(),localized,complete);
                }
                catch(OperationCanceledException)when(job.IsCancellationRequested||packet.Discarded) { packet.Chunks.Writer.TryComplete(new OperationCanceledException());UpdateCurrent(item,e=>e.SynthesisState="Cancelled"); }
                catch(Exception e)
                {
                    UpdateCurrent(item,x=> { x.SynthesisState="Failed";x.PlaybackState="Unspoken";x.Error=e.Message;x.CancellationReason="Synthesis failed; Hindi retained"; });
                    packet.Chunks.Writer.TryComplete(e);prepared.TryRemove(item.Id,out _);Error?.Invoke(e.Message);
                }
                Wake();
            }
        }
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested) { }
    }
    private async Task PlaybackLoop()
    {
        try
        {
            while(!lifetime.IsCancellationRequested)
            {
                if((paused&&!catchingUp)||(needsCatchup&&!catchingUp)) { await Tick();continue; }
                var item=store.First(scene,"playback IN ('Pending','Ready','Deferred')");if(item is null) { catchingUp=false;await Tick();continue; }
                if(Expired(item)) { Unspoken(item,"Live Sync playback deadline; full translated line remains in history");continue; }
                if(item.PlaybackState!="Ready"||!prepared.TryGetValue(item.Id,out var packet)) { await Tick();continue; }
                if(policy==DialoguePolicy.StoryComplete&&!catchingUp&&(DateTimeOffset.UtcNow-item.AppearedAt).TotalSeconds>8)
                { needsCatchup=true;Status?.Invoke($"Story backlog: {PendingCount} lines remembered. Use Catch up or replay History; nothing was silently deleted.");continue; }
                using var job=JobToken(item.Scene);var began=false;
                try
                {
                    await foreach(var clip in packet.Chunks.Reader.ReadAllAsync(job.Token))
                    {
                        while(paused&&!catchingUp)await Task.Delay(20,job.Token);
                        job.Token.ThrowIfCancellationRequested();
                        await pipeline.SchedulerPlayer.PlayAsync(clip,()=>
                        {
                            if(began)return;began=true;var now=DateTimeOffset.UtcNow;var delay=(now-item.AppearedAt).TotalMilliseconds;
                            UpdateCurrent(item,e=> { e.PlaybackState="Playing";e.PlaybackStartedAt=now; });
                            pipeline.PublishPlaying(item.ToDialogue(),new(item.Hindi,"neutral",item.TranslationSource));
                            pipeline.Metrics.Add(new(now,pipeline.SchedulerVoice.Identity,false,item.TranslationMs,item.SynthesisMs,delay,0) { ObservedToAudioMs=delay });
                            pipeline.Trace?.Write("playback_started",item.ToDialogue(),new { voice=item.VoiceId,speaker=item.Speaker,observedToAudioMs=delay,queueWaitMs=item.ReadyAt is null?0:(now-item.ReadyAt.Value).TotalMilliseconds });
                        },job.Token);
                    }
                    job.Token.ThrowIfCancellationRequested();UpdateCurrent(item,e=> { e.PlaybackState="Completed";e.PlaybackCompletedAt=DateTimeOffset.UtcNow; });
                }
                catch(OperationCanceledException)when(job.IsCancellationRequested) { }
                catch(Exception e) { UpdateCurrent(item,x=> { x.PlaybackState="Unspoken";x.CancellationReason="Playback failed; Hindi retained";x.Error=e.Message; });Error?.Invoke(e.Message); }
                finally { prepared.TryRemove(item.Id,out _);Wake(); }
            }
        }
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested) { }
    }
    public async Task WaitForIdleAsync(CancellationToken token)
    {
        while(store.Count(scene,"translation IN ('Pending','Translating') OR playback IN ('Pending','Ready','Playing','Deferred')")>0) { await Task.Delay(30,token); }
    }
    public async ValueTask DisposeAsync()
    {
        lock(gate)
        {
            if(Interlocked.Exchange(ref disposed,1)!=0)return;
            lifetime.Cancel();SkipSceneCore("Session stopped; dialogue retained in History");
        }
        await Task.WhenAll(workers);sceneCancellation.Dispose();lifetime.Dispose();pulse.Dispose();
    }
}

public sealed class TranslationTextCache(string directory)
{
    private static readonly object gate=new();
    public static string Hash(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string Identity(Dialogue line,GameProfile profile,string engine,IReadOnlyList<string> history)=>Hash(JsonSerializer.Serialize(new { line.English,line.Speaker,line.Context,profile.Glossary,profile.ReviewedLines,engine,history }));
    public LocalizedLine? Get(string key)
    {
        lock(gate)try { var path=Path.Combine(directory,key+".json");if(!File.Exists(path))return null;return Json.Read<LocalizedLine>(path); } catch(Exception e)when(e is IOException or JsonException) { return null; }
    }
    public void Put(string key,LocalizedLine line)
    {
        lock(gate)
        {
            Directory.CreateDirectory(directory);Json.Write(Path.Combine(directory,key+".json"),line);
            var files=new DirectoryInfo(directory).GetFiles("*.json").OrderBy(f=>f.LastWriteTimeUtc).ToArray();var total=files.Sum(f=>f.Length);
            var count=files.Length;
            foreach(var f in files) { if(total<=16*1024*1024&&count<=2000)break;total-=f.Length;count--;f.Delete(); }
        }
    }
}
