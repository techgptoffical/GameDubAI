using GameDub.Core;
using System.Collections.Concurrent;

internal static class V2Acceptance
{
    private static void Check(bool ok,string message) { if(!ok)throw new Exception(message); }
    private static async Task Until(Func<bool> condition,int timeout=8000)
    {
        using var limit=new CancellationTokenSource(timeout);
        while(!condition())await Task.Delay(10,limit.Token);
    }
    public static async Task Run(string root,Func<string,Func<Task>,Task> test)
    {
        await test("V2 50 mixed-speaker events persist and play in order with bounded preparation",async()=>
        {
            var dir=Path.Combine(root,"v2-fifty");using var store=new DialogueEventStore(Path.Combine(dir,"events.sqlite"));
            var output=new Output(25);var voice=new Voice(4,()=>output.Active>0);
            var profile=new GameProfile { DefaultVoice="hm_omega",Voices=new() { ["Michael"]="hm_omega",["Amanda"]="hf_beta" } };
            var pipeline=new DubbingPipeline(new Translate(),voice,output,new(Path.Combine(dir,"audio")),profile);
            await using var scheduler=new StoryDialogueScheduler(pipeline,store,translationCacheDirectory:Path.Combine(dir,"mt"));
            scheduler.Pause("simulate a pause while captions arrive");
            var lines=Enumerable.Range(0,50).Select(i=>new Dialogue($"unseen line {i}",i%2==0?"Michael":"Amanda")).ToArray();
            foreach(var line in lines) { scheduler.Submit(line);scheduler.SubtitleCleared(); }
            scheduler.Submit(lines[0]); // Same event ID must never be inserted twice.
            await Until(()=>store.Recent(100).All(e=>e.TranslationState=="Ready")&&scheduler.PreparedCount==3);
            Check(store.Recent(100).Length==50,"Lost persisted events or inserted duplicate");
            Check(output.Lines.Count==0,"Played while paused");scheduler.CatchUp();
            var max=0;using var timeout=new CancellationTokenSource(12000);
            var idle=scheduler.WaitForIdleAsync(timeout.Token);
            while(!idle.IsCompleted) { max=Math.Max(max,scheduler.PreparedCount);await Task.Delay(5,timeout.Token); }
            await idle;
            var events=store.Recent(100).OrderBy(e=>e.Sequence).ToArray();
            Check(events.All(e=>e.PlaybackState=="Completed"&&e.DisappearedAt is not null&&e.AudioCacheKey is not null),"Incomplete event state");
            Check(output.Lines.SequenceEqual(lines.Select(l=>l.English)),"Playback skipped or reordered dialogue");
            Check(output.MaxActive==1&&max<=3&&scheduler.DroppedCount==0,"Overlap, unbounded preparation or silent drop");
            Check(voice.DuringPlayback>0,"Did not prepare future speech during current playback");
            Check(events.Select(e=>e.VoiceId).SequenceEqual(lines.Select((_,i)=>i%2==0?"hm_omega":"hf_beta")),"Speaker voice changed");
            Json.Write(Path.Combine(dir,"acceptance.json"),new { syntheticProviders=true,eventCount=events.Length,maxPrepared=max,output.MaxActive,voice.DuringPlayback,events });
        });
        await test("V2 Story Complete retains old lines until explicit catch-up",async()=>
        {
            var dir=Path.Combine(root,"v2-catchup");using var store=new DialogueEventStore(Path.Combine(dir,"events.sqlite"));var output=new Output(10);
            await using var scheduler=new StoryDialogueScheduler(new(new Translate(),new Voice(),output,new(Path.Combine(dir,"audio")),new()),store,translationCacheDirectory:Path.Combine(dir,"mt"));
            var line=new Dialogue("remember this story") { FirstSeenAt=DateTimeOffset.UtcNow.AddSeconds(-25) };scheduler.Submit(line);
            await Until(()=>scheduler.NeedsCatchup);Check(output.Lines.IsEmpty&&store.Get(line.EventId)!.Hindi.Length>0,"Old line lost or played without catch-up");
            scheduler.CatchUp();using var timeout=new CancellationTokenSource(3000);await scheduler.WaitForIdleAsync(timeout.Token);
            Check(store.Get(line.EventId)!.PlaybackState=="Completed"&&scheduler.DroppedCount==0,"Catch-up dropped the line");
        });
        await test("V2 Live Sync expires audio but retains every translated caption",async()=>
        {
            var dir=Path.Combine(root,"v2-live");using var store=new DialogueEventStore(Path.Combine(dir,"events.sqlite"));var output=new Output(10);
            await using var scheduler=new StoryDialogueScheduler(new(new Translate(),new Voice(250),output,new(Path.Combine(dir,"audio")),new()),store,DialoguePolicy.LiveSync,100,Path.Combine(dir,"mt")) { LiveNewestLimitMs=100 };
            var line=new Dialogue("slow generation deadline");scheduler.Submit(line);
            await Until(()=>store.Get(line.EventId)!.PlaybackState=="Unspoken");await Task.Delay(400);
            var e=store.Get(line.EventId)!;Check(e.PlaybackState=="Unspoken"&&e.Hindi.Length>0&&e.CancellationReason!.Contains("deadline"),"Expired audio resurrected or caption lost");
            Check(output.Lines.IsEmpty&&scheduler.PreparedCount==0,"Expired buffer played or stayed resident");
        });
        await test("Live on a busy PC: the newest late line still speaks, older late lines give way",async()=>
        {
            var dir=Path.Combine(root,"v2-live-late");using var store=new DialogueEventStore(Path.Combine(dir,"events.sqlite"));var output=new Output(10);
            await using var scheduler=new StoryDialogueScheduler(new(new Translate(),new Voice(250),output,new(Path.Combine(dir,"audio")),new()),store,DialoguePolicy.LiveSync,100,Path.Combine(dir,"mt"));
            var newest=new Dialogue("late but newest");scheduler.Submit(newest);
            await Until(()=>store.Get(newest.EventId)!.PlaybackState is "Completed" or "Unspoken");
            Check(store.Get(newest.EventId)!.PlaybackState=="Completed","Newest line dropped although nothing newer was waiting: "+store.Get(newest.EventId)!.CancellationReason);
            var a=new Dialogue("older late A");var b=new Dialogue("newer B");scheduler.Submit(a);scheduler.Submit(b);
            await Until(()=>store.Get(b.EventId)!.PlaybackState is "Completed" or "Unspoken");
            Check(store.Get(a.EventId)!.PlaybackState=="Unspoken"&&store.Get(b.EventId)!.PlaybackState=="Completed","Older late line kept or newest dropped: "+store.Get(a.EventId)!.PlaybackState+"/"+store.Get(b.EventId)!.PlaybackState);
        });
        await test("V2 explicit scene skip cancels old playback and keeps history",async()=>
        {
            var dir=Path.Combine(root,"v2-skip");using var store=new DialogueEventStore(Path.Combine(dir,"events.sqlite"));var output=new Output(400);
            await using var scheduler=new StoryDialogueScheduler(new(new Translate(),new Voice(5),output,new(Path.Combine(dir,"audio")),new()),store,translationCacheDirectory:Path.Combine(dir,"mt"));
            var a=new Dialogue("old scene A");var b=new Dialogue("old scene B");scheduler.Submit(a);scheduler.Submit(b);await Until(()=>output.Active>0);
            scheduler.SkipScene("User skipped cutscene");scheduler.Submit(new("new scene"));
            using var timeout=new CancellationTokenSource(4000);await scheduler.WaitForIdleAsync(timeout.Token);
            Check(store.Get(a.EventId)!.PlaybackState=="Cancelled"&&store.Get(b.EventId)!.PlaybackState=="Cancelled","Old scene status resurrected");
            Check(store.Get(a.EventId)!.CancellationReason=="User skipped cutscene"&&output.Lines.Last()=="new scene","Skip reason lost or new scene failed");
        });
        await test("V2 pausing active speech preserves its remainder and stops the next line",async()=>
        {
            var dir=Path.Combine(root,"v2-pause");using var store=new DialogueEventStore(Path.Combine(dir,"events.sqlite"));var output=new Output(250);
            await using var scheduler=new StoryDialogueScheduler(new(new Translate(),new Voice(),output,new(Path.Combine(dir,"audio")),new()),store,translationCacheDirectory:Path.Combine(dir,"mt"));
            var a=new Dialogue("pause A");scheduler.Submit(a);scheduler.Submit(new("pause B"));await Until(()=>output.Active>0);
            scheduler.Pause("game in menu");await Task.Delay(350);
            Check(output.Lines.Count==1&&store.Get(a.EventId)!.PlaybackState=="Playing","Pause discarded current remainder or began next line");
            scheduler.Resume();using var timeout=new CancellationTokenSource(3000);await scheduler.WaitForIdleAsync(timeout.Token);
            Check(output.Lines.SequenceEqual(new[]{"pause A","pause B"}),"Resume restarted or skipped a line");
        });
        await test("V2 voice assignment uses labels then manual selection then stable default",()=>
        {
            var profile=new GameProfile { DefaultVoice="hm_omega",ManualSpeaker="Amanda",Voices=new() { ["Michael"]="hm_psi",["Amanda"]="hf_beta" } };
            Check(CharacterVoices.Assign(new("I'm afraid.","michael"),profile).VoiceId=="hm_psi","Case insensitive explicit label lost");
            Check(CharacterVoices.Assign(new("I'm angry."),profile).VoiceId=="hf_beta","Manual speaker ignored");profile.ManualSpeaker="Unknown";
            var unknown=CharacterVoices.Assign(new("She told him to run!"),profile);
            Check(unknown.VoiceId=="hm_omega"&&unknown.Speaker=="Unknown"&&unknown.Confidence is null,"Guessed gender from dialogue");
            profile.GameId="GTA5_Enhanced";CharacterVoices.MigrateKnownGame(profile);
            Check(profile.VoiceFor("Michael")=="hm_psi","Migration overwrote user assignment");return Task.CompletedTask;
        });
        await test("Live Cinematic keeps speech near the scene: bounded backlog, order, no overlap, history kept",async()=>
        {
            var dir=Path.Combine(root,"live-cinematic");using var store=new DialogueEventStore(Path.Combine(dir,"events.sqlite"));var output=new Output(400);
            await using var scheduler=new StoryDialogueScheduler(new(new Translate(),new Voice(),output,new(Path.Combine(dir,"audio")),new()),store,DialoguePolicy.LiveSync,6000,Path.Combine(dir,"mt"));
            var lines=Enumerable.Range(0,8).Select(i=>new Dialogue($"rapid cutscene line {i}")).ToArray();
            var maxWaiting=0L;
            foreach(var line in lines) { scheduler.Submit(line with { FirstSeenAt=DateTimeOffset.UtcNow });await Task.Delay(60);maxWaiting=Math.Max(maxWaiting,store.Count(0,"playback IN ('Pending','Ready','Deferred')")); }
            using var timeout=new CancellationTokenSource(8000);await scheduler.WaitForIdleAsync(timeout.Token);
            var events=lines.Select(l=>store.Get(l.EventId)!).ToArray();var played=output.Lines.ToArray();
            Check(maxWaiting<=2,"Live backlog exceeded 2 waiting lines: "+maxWaiting);
            Check(played.Length>=2&&played.Length<lines.Length,"Live mode either spoke nothing or let the whole burst queue up");
            Check(played.SequenceEqual(played.OrderBy(p=>int.Parse(p.Split(' ').Last()))),"Live playback reordered dialogue");
            Check(played.Last()==lines.Last().English,"Newest line was not spoken");
            Check(output.MaxActive==1,"Overlapping speech");
            Check(events.All(e=>e.Hindi.Length>0)&&events.Count(e=>e.PlaybackState=="Unspoken")==lines.Length-played.Length,"Skipped lines lost their translated text in History");
            var lag=events.Where(e=>e.PlaybackStartedAt is not null).Max(e=>(e.PlaybackStartedAt!.Value-e.AppearedAt).TotalMilliseconds);
            Json.Write(Path.Combine(dir,"live.json"),new { played,maxWaiting,maxStartLagMs=lag,events=events.Select(e=>new { e.English,e.PlaybackState,e.CancellationReason,e.AppearedAt,e.ReadyAt,e.PlaybackStartedAt,e.PlaybackCompletedAt,e.TranslationMs,e.SynthesisMs }) });
            Check(lag<3000,"Live start lag too high (fake lines play ~1.2 s each, at most 2 may wait): "+lag);
        });
        await test("V2 event history survives database reopen",()=>
        {
            var path=Path.Combine(root,"v2-persistence","events.sqlite");var line=new Dialogue("stored English");
            using(var store=new DialogueEventStore(path)) { store.Add(line,0,new("Unknown","hm_omega","default",null));store.Update(line.EventId,e=> { e.Hindi="यह याद रहेगा।";e.PlaybackState="Completed"; }); }
            using(var reopened=new DialogueEventStore(path))Check(reopened.Get(line.EventId)?.Hindi=="यह याद रहेगा।"&&reopened.Recent().Length==1,"History lost on reopen");
            return Task.CompletedTask;
        });
        await test("V2 provider failures retain dialogue and later lines recover",async()=>
        {
            var dir=Path.Combine(root,"v2-failure");using var store=new DialogueEventStore(Path.Combine(dir,"events.sqlite"));var output=new Output(10);
            await using var scheduler=new StoryDialogueScheduler(new(new Translate(),new Voice(),output,new(Path.Combine(dir,"audio")),new()),store,translationCacheDirectory:Path.Combine(dir,"mt"));
            var bad=new Dialogue("fail");scheduler.Submit(bad);scheduler.Submit(new("recover"));using var timeout=new CancellationTokenSource(3000);await scheduler.WaitForIdleAsync(timeout.Token);
            Check(store.Get(bad.EventId)!.TranslationState=="Failed"&&output.Lines.Single()=="recover","Failure erased event or wedged queue");
        });
        await test("V2 repeated fresh events reuse translation and stopped callbacks are harmless",async()=>
        {
            var dir=Path.Combine(root,"v2-stop");using var store=new DialogueEventStore(Path.Combine(dir,"events.sqlite"));var output=new Output(10);var mt=new Translate();
            var scheduler=new StoryDialogueScheduler(new(mt,new Voice(),output,new(Path.Combine(dir,"audio")),new()),store,translationCacheDirectory:Path.Combine(dir,"mt"));
            var first=new Dialogue("repeat this");scheduler.Submit(first);using var timeout=new CancellationTokenSource(3000);await scheduler.WaitForIdleAsync(timeout.Token);
            scheduler.SkipScene("repeat in a new scene");scheduler.Submit(new("repeat this"));await scheduler.WaitForIdleAsync(timeout.Token);
            Check(mt.Calls==1&&output.Lines.Count==2,"Cache missed stable content or fresh repeat suppressed");
            await scheduler.DisposeAsync();scheduler.Pause("late callback");scheduler.Resume();scheduler.Interrupt();scheduler.SubtitleCleared();scheduler.Submit(new("late"));await scheduler.DisposeAsync();
            Check(store.Recent().Length==2,"Stopped source inserted dialogue");
        });
    }
    private sealed class Translate : ILocalizer
    {
        public int Calls;public string Identity=>"v2-test-translation";
        public async Task<LocalizedLine> TranslateAsync(Dialogue line,GameProfile profile,IReadOnlyList<string> history,CancellationToken token)
        { Interlocked.Increment(ref Calls);await Task.Delay(2,token);if(line.English=="fail")throw new IOException("test translation failure");return new(line.English,"neutral",Identity); }
    }
    private sealed class Voice(int delay=2,Func<bool>? playing=null) : IVoiceProvider
    {
        public int DuringPlayback;public string Identity=>"v2-test-voice";
        public async Task<AudioClip> SynthesizeAsync(LocalizedLine line,string voice,CancellationToken token)
        { if(playing?.Invoke()==true)Interlocked.Increment(ref DuringPlayback);await Task.Delay(delay,token);return new(new byte[480],"pcm_24000",line.Hindi); }
    }
    private sealed class Output(int delay) : IAudioPlayer,IPausableAudioPlayer
    {
        public readonly ConcurrentQueue<string> Lines=new();public int Active,MaxActive;private volatile bool paused;
        public async Task PlayAsync(AudioClip clip,Action started,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();var active=Interlocked.Increment(ref Active);MaxActive=Math.Max(MaxActive,active);
            try { while(paused)await Task.Delay(5,token);Lines.Enqueue(clip.Identity);started();for(var i=0;i<delay;i+=5) { while(paused)await Task.Delay(5,token);await Task.Delay(5,token); } }
            finally { Interlocked.Decrement(ref Active); }
        }
        public void Stop() { paused=false; }public void Pause()=>paused=true;public void Resume()=>paused=false;
    }
}
