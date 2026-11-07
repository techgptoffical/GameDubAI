using System.Diagnostics;
using System.Drawing;
using GameDub.Core;
using NAudio.Wave;
using NAudio.CoreAudioApi;

if (args.Contains("--audio-diagnostics"))
{
    using var enumerator = new MMDeviceEnumerator();
    using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    var appPids = Process.GetProcessesByName("GameDub.App").Select(p => { using(p) return (uint)p.Id; }).ToHashSet();
    var endpoints = new List<object>();
    foreach(var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
    {
        using(device)
        {
            var sessions = new List<object>();
            try
            {
                var collection = device.AudioSessionManager.Sessions;
                for(var i=0;i<collection.Count;i++)
                {
                    using var session = collection[i];
                    if(appPids.Contains(session.GetProcessID))
                        sessions.Add(new { pid = session.GetProcessID, volume = session.SimpleAudioVolume.Volume, muted = session.SimpleAudioVolume.Mute });
                }
            }
            catch(System.Runtime.InteropServices.COMException) { }
            float? volume = null; bool? muted = null;
            try { volume = device.AudioEndpointVolume.MasterVolumeLevelScalar; muted = device.AudioEndpointVolume.Mute; }
            catch(System.Runtime.InteropServices.COMException) { }
            endpoints.Add(new { name = device.FriendlyName, isDefault = device.ID == defaultDevice.ID,
                volume, muted, gameDubSessions = sessions });
        }
    }
    var report = new { endpoints, selectedGameForeground = GameWindows.List().Where(GameWindows.IsLikelyGame).Select(g=>new { g.Name, g.ProcessId, foreground = GameWindows.GetForegroundWindow()==g.Handle }) };
    Json.Write(Path.Combine(Paths.Workspace,"artifacts","live-audio-diagnostics.json"),report);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report)); return 0;
}

if (args.Contains("--audio-source-child"))
{
    if (Array.IndexOf(args, "--release-path") is var releaseIndex && releaseIndex >= 0)
    {
        var releasePath = args[releaseIndex + 1];
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        while (!File.Exists(releasePath)) await Task.Delay(50, wait.Token);
    }
    else await Task.Delay(2500);
    var info = new ProcessStartInfo(EspeakVoice.Executable!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardInput = true };
    foreach (var arg in new[] { "--stdin", "--stdout", "-v", "en-us", "-s", "130" }) info.ArgumentList.Add(arg);
    using var speech = Process.Start(info)!; using var buffer = new MemoryStream();
    var read = speech.StandardOutput.BaseStream.CopyToAsync(buffer);
    await speech.StandardInput.WriteLineAsync("The bridge is broken. Find another way. We need to get out of here. The bridge is broken. Find another way."); speech.StandardInput.Close();
    await Task.WhenAll(read, speech.WaitForExitAsync());
    using var output = new WasapiPlayer { Volume = .25f };
    await output.PlayAsync(new(buffer.ToArray(), "wav", "synthetic-English"), () => { }, default);
    await Task.Delay(2000);
    return 0;
}

if (args.Contains("--subtitle-source-child"))
{
    var form = new System.Windows.Forms.Form { Text = "GameDub automated synthetic subtitle fixture", ClientSize = new Size(1000, 220), BackColor = Color.Black, StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen };
    var label = new System.Windows.Forms.Label { Dock = System.Windows.Forms.DockStyle.Fill, Text = "We gotta get out of here!", ForeColor = Color.White, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 28) };
    form.Controls.Add(label);
    var timer = new System.Windows.Forms.Timer { Interval = 1800 }; var frame = 0;
    // The first subtitle disappears before normal neural generation completes.
    // Regression: a blank frame used to cancel it and produce total silence.
    timer.Tick += (_, _) => { if (frame == 0 && GameWindows.GetForegroundWindow() != form.Handle) return; frame++; label.Text = frame switch { 1 => "", 2 => "The bridge is broken. Find another way.", _ => "" }; timer.Interval = 7500; if (frame >= 4) form.Close(); };
    form.Shown += (_, _) => timer.Start();
    System.Windows.Forms.Application.Run(form); timer.Dispose(); form.Dispose(); return 0;
}
if (args.Contains("--window-recreation-child"))
{
    var context = new System.Windows.Forms.ApplicationContext();
    var first = new PassiveTestWindow { Text="GameDub window fixture — first", ClientSize=new Size(360,180) };
    var second = new PassiveTestWindow { Text="GameDub window fixture — second", ClientSize=new Size(480,220) };
    first.Show(); second.Show();
    var timer = new System.Windows.Forms.Timer { Interval=2500 }; var tick=0;
    timer.Tick += (_,_) => { tick++; if(tick==1) first.Close(); else { second.Close(); context.ExitThread(); } };
    timer.Start(); System.Windows.Forms.Application.Run(context); timer.Dispose(); first.Dispose(); second.Dispose(); context.Dispose(); return 0;
}
if (args.Contains("--list-game-windows"))
{
    var windows = GameWindows.List().Where(GameWindows.IsLikelyGame).Select(g=>new { g.ProcessId, g.Name, g.Title, handle=g.Handle.ToInt64(), alive=GameWindows.IsAlive(g) }).ToArray();
    Json.Write(Path.Combine(Paths.Workspace,"artifacts","game-window-diagnostics.json"),windows);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(windows)); return 0;
}

var results = new List<object>();
var failed = 0;
var sourceExecutable = Path.Combine(Paths.Workspace, "tools", "GameDub.TestSource", "bin", "Debug", "net8.0-windows10.0.19041.0", "GameDub.TestSource.exe");
async Task Test(string name, Func<Task> body)
{
    var timer = Stopwatch.StartNew();
    try { await body(); results.Add(new { name, passed = true, ms = timer.Elapsed.TotalMilliseconds }); Console.WriteLine($"PASS {name} ({timer.ElapsedMilliseconds} ms)"); }
    catch (Exception e) { failed++; results.Add(new { name, passed = false, error = e.ToString(), ms = timer.Elapsed.TotalMilliseconds }); Console.WriteLine($"FAIL {name}: {e.Message}"); }
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
var testRoot = Path.Combine(Paths.Workspace, "artifacts", "tests", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(testRoot);

await Test("closed demo selection resolves to the actual GTA process, not the Steam title", () =>
{
    var staleDemo = new GameWindow(10,11,"GameDub-demo","Synthetic test");
    var steam = new GameWindow(20,21,"steamwebhelper","Grand Theft Auto V Enhanced");
    var gta = new GameWindow(30,31,"GTA5_Enhanced","Grand Theft Auto V");
    Check(GameWindows.ChooseSelection(new[] { steam,gta },staleDemo)==gta,"Selected a launcher/demo instead of GTA");
    Check(!GameWindows.IsLikelyGame(steam),"Steam title was treated as a game process"); return Task.CompletedTask;
});
await Test("no matching game clears a dead target and multiple games require explicit selection", () =>
{
    var stale = new GameWindow(1,2,"GameDub-demo","Closed test");
    var browser = new GameWindow(3,4,"chrome","Browser");
    Check(GameWindows.ChooseSelection(new[] { browser },stale) is null,"Silently selected an unrelated window");
    var games = new[] {new GameWindow(5,6,"GTA5_Enhanced","GTA"),new GameWindow(7,8,"rdr2","RDR2")};
    Check(GameWindows.ChooseSelection(games,stale) is null,"Ambiguous game auto-selection"); return Task.CompletedTask;
});
await Test("manual unknown-game selection survives refresh and replacement handles keep process identity", () =>
{
    var game = new GameWindow(10,11,"UnknownStoryGame","An unlisted game");
    var replacement = game with { Handle=12 };
    Check(GameWindows.ChooseSelection(new[] {game},game)==game,"Lost manually selected unlisted game");
    Check(GameWindows.ChooseSelection(new[] {replacement},game)==replacement,"Did not resolve recreated window");
    Check(GameWindows.ChooseSelection(new[] {replacement with {ProcessId=99}},game) is null,"Rebound to a different unknown process"); return Task.CompletedTask;
});
if (args.Contains("--windows"))
{
    await Test("native enumeration discovers multiple windows and rebinds a closed HWND without leaving its process", async () =>
    {
        using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList={"--window-recreation-child"}, UseShellExecute=false,CreateNoWindow=true })!;
        try
        {
            List<GameWindow> windows = new();
            for(var i=0;i<30;i++) { windows=GameWindows.List().Where(w=>w.ProcessId==child.Id).ToList(); if(windows.Count>=2)break; await Task.Delay(50); }
            Check(windows.Count>=2,"Native picker omitted a secondary game window");
            var first=windows.Single(w=>w.Title.EndsWith("first"));
            Check(GameWindows.IsAlive(first) && !GameWindows.IsAlive(first with { ProcessId=first.ProcessId+1 }),"Window ownership was not checked");
            for(var i=0;i<60 && GameWindows.IsAlive(first);i++)await Task.Delay(50);
            var rebound=GameWindows.Rebind(first);
            Check(rebound is not null && rebound.ProcessId==child.Id && rebound.Title.EndsWith("second"),"Could not reattach to surviving game window");
        }
        finally { if(!child.HasExited)child.Kill(true); }
    });
}

await Test("dataset contains >=30 contextual Hindi lines", () =>
{
    var lines = Json.Samples(); Check(lines.Count >= 30, "Missing test lines"); Check(lines.All(l => l.Hindi.Any(c => c is >= '\u0900' and <= '\u097F')), "Non-Hindi sample"); return Task.CompletedTask;
});
await Test("subtitle stabilization and duplicate suppression", () =>
{
    var d = new SubtitleChangeDetector();
    Check(d.Observe("We gotta get out of here!", TimeSpan.Zero).Event == SubtitleEvent.None, "First frame emitted");
    Check(d.Observe("We gotta get out of here!", TimeSpan.FromMilliseconds(250)).Event == SubtitleEvent.Line, "Stable line missed");
    for (var i = 1; i < 100; i++) Check(d.Observe("We gotta get out of here!", TimeSpan.FromSeconds(i)).Event == SubtitleEvent.None, "Duplicate emitted");
    return Task.CompletedTask;
});
await Test("partial subtitles wait and clearing allows a later repeat", () =>
{
    var d = new SubtitleChangeDetector(); d.Observe("We gotta", TimeSpan.Zero);
    Check(d.Observe("We gotta", TimeSpan.FromMilliseconds(300)).Event == SubtitleEvent.None, "Partial too early");
    d.Observe("We gotta get out!", TimeSpan.FromMilliseconds(400));
    Check(d.Observe("We gotta get out!", TimeSpan.FromMilliseconds(700)).Event == SubtitleEvent.Line, "Final line missed");
    d.Observe("", TimeSpan.FromMilliseconds(800));
    Check(d.Observe("", TimeSpan.FromMilliseconds(1550)).Event == SubtitleEvent.Cleared, "Clear missed");
    d.Observe("We gotta get out!", TimeSpan.FromMilliseconds(1600));
    Check(d.Observe("We gotta get out!", TimeSpan.FromMilliseconds(1900)).Event == SubtitleEvent.Line, "Replay wrongly suppressed");
    return Task.CompletedTask;
});
await Test("OCR punctuation and spacing changes do not restart a confirmed line", () =>
{
    var d = new SubtitleChangeDetector(); d.Observe("The guy's your neighbor.", TimeSpan.Zero);
    Check(d.Observe("The guy's your neighbor.", TimeSpan.FromMilliseconds(300)).Event == SubtitleEvent.Line, "Initial line missing");
    d.Observe("•The-guy'syour neighbor.", TimeSpan.FromMilliseconds(500));
    Check(d.Observe("•The-guy'syour neighbor.", TimeSpan.FromMilliseconds(1000)).Event == SubtitleEvent.None, "OCR formatting restarted translation");
    d.Observe("The guy is not your neighbor.", TimeSpan.FromMilliseconds(1200));
    Check(d.Observe("The guy is not your neighbor.", TimeSpan.FromMilliseconds(1500)).Event == SubtitleEvent.Line, "Changed meaning was incorrectly suppressed");
    return Task.CompletedTask;
});
await Test("speaker parsing and safe unknown identity", () =>
{
    Check(Text.Parse("Ravi: Stop!").Speaker == "Ravi", "Label lost"); Check(Text.Parse("Don't move.").Speaker == "Unknown", "Invented speaker"); return Task.CompletedTask;
});
await Test("context memory bounded and resettable", () =>
{
    var memory = new DialogueContextMemory(); for (var i = 0; i < 20; i++) memory.Add(new(i.ToString()), new("हिंदी", "neutral", "test"));
    Check(memory.Snapshot().Count == 6, "Unbounded context"); memory.Clear(); Check(memory.Snapshot().Count == 0, "Reset failed"); return Task.CompletedTask;
});
await Test("context, voices, glossary and model version invalidate cache", () =>
{
    var p = new GameProfile(); var l = new Dialogue("test");
    string Key(string voice = "a", string tts = "v1", string[]? history = null) => DialogueCache.Identity(l, p, "mt", tts, voice, history ?? []);
    var first = Key(); Check(first != Key("b") && first != Key(tts: "v2") && first != Key(history: ["prior line"]), "Cache collision");
    p.Glossary["Arthur"] = "आर्थर"; Check(first != Key(), "Glossary ignored"); return Task.CompletedTask;
});
await Test("cache roundtrip, bounded eviction and corrupt-entry recovery", () =>
{
    var directory = Path.Combine(testRoot, "cache"); var cache = new DialogueCache(directory, 12000);
    var line = new LocalizedLine("हिंदी", "neutral", "test"); var clip = new AudioClip(new byte[2000], "pcm_24000", "test");
    cache.Put("one", line, clip); Check(cache.Get("one")?.Clip.Bytes.Length == 2000, "Cache mismatch");
    for (var i = 0; i < 10; i++) cache.Put("entry" + i, line, clip);
    Check(new DirectoryInfo(directory).GetFiles().Sum(f => f.Length) <= 12000, "Cache grew beyond limit");
    File.WriteAllText(Path.Combine(directory, "broken.json"), "broken"); Check(cache.Get("broken") is null, "Corrupt cache crashed"); return Task.CompletedTask;
});
await Test("unknown fixture never pretends to translate", async () =>
{
    try { await new FixtureLocalizer().TranslateAsync(new("An unseen line."), new(), [], default); throw new Exception("Unknown accepted"); }
    catch (InvalidOperationException) { }
});
await Test("ambiguous fixture requires explicit context", async () =>
{
    try { await new FixtureLocalizer().TranslateAsync(new("Give me a break."), new(), [], default); throw new Exception("Ambiguous accepted"); }
    catch (InvalidOperationException) { }
    var sample = Json.Samples().First(s => s.Id == "18");
    var line = await new FixtureLocalizer().TranslateAsync(new(sample.English, sample.Speaker, sample.Emotion, sample.Context), new(), [], default);
    Check(line.Hindi.Contains("आराम"), "Context variant lost");
});
await Test("latest-line routing cancels old work and bounds backlog", async () =>
{
    var fake = new FakePlayer(); var pipeline = new DubbingPipeline(new FakeLocalizer(150), new FakeVoice(), fake, new(Path.Combine(testRoot, "route")), new());
    await using var router = new DialogueRouter(pipeline, fake);
    router.Submit(new("first")); await Task.Delay(30);
    for (var i = 0; i < 1000; i++) router.Submit(new("line" + i));
    await Task.Delay(600);
    Check(fake.Plays == 1 && fake.Last == "line999", $"Obsolete speech/backlog: {fake.Plays}, {fake.Last}");
});
await Test("stop cancels translation and produces no playback", async () =>
{
    var fake = new FakePlayer(); var pipeline = new DubbingPipeline(new FakeLocalizer(500), new FakeVoice(), fake, new(Path.Combine(testRoot, "stop")), new());
    await using var router = new DialogueRouter(pipeline, fake); router.Submit(new("old")); await Task.Delay(30); router.Interrupt(); await Task.Delay(100);
    Check(fake.Plays == 0, "Playback after stop");
});
await Test("short subtitle disappearance allows confirmed speech but a replacement cancels it", async () =>
{
    var player = new FakePlayer();
    var pipeline = new DubbingPipeline(new FakeLocalizer(150), new FakeVoice(), player, new(Path.Combine(testRoot,"subtitle-gap")), new());
    await using var router = new DialogueRouter(pipeline, player);
    IDialogueSink sink = router;
    sink.Submit(new("brief subtitle")); await Task.Delay(30); sink.SubtitleCleared();
    await Task.Delay(300);
    Check(player.Plays == 1 && player.Last == "brief subtitle", "Blank frame cancelled confirmed Hindi");
    sink.Submit(new("obsolete")); await Task.Delay(30); sink.SubtitleCleared(); sink.Submit(new("replacement"));
    await Task.Delay(350);
    Check(player.Plays == 2 && player.Last == "replacement", "Gap allowed obsolete work to survive a replacement");
});
await Test("automatic subtitle gap retains pending Hindi and keeps audio holdoff", async () =>
{
    var player = new FakePlayer();
    var pipeline = new DubbingPipeline(new FakeLocalizer(150), new FakeVoice(), player, new(Path.Combine(testRoot,"hybrid-gap")), new());
    await using var router = new DialogueRouter(pipeline, player);
    var arbiter = new HybridArbiter(router);
    arbiter.Subtitles.Submit(new("subtitle")); await Task.Delay(30); arbiter.Subtitles.SubtitleCleared();
    arbiter.Audio.Submit(new("immediate mixed audio")); await Task.Delay(300);
    Check(player.Plays == 1 && player.Last == "subtitle", "Audio replaced subtitle during the gap holdoff");
    arbiter.Subtitles.Submit(new("interrupted")); await Task.Delay(30); arbiter.Subtitles.Interrupt(); await Task.Delay(250);
    Check(player.Plays == 1, "Hard interruption stopped working");
});
await Test("stale generation budget drops delayed speech", async () =>
{
    var fake = new FakePlayer(); var pipeline = new DubbingPipeline(new FakeLocalizer(500), new FakeVoice(), fake, new(Path.Combine(testRoot, "stale")), new(), 40);
    try { await pipeline.ProcessAsync(new("stale"), default); throw new Exception("Stale accepted"); } catch (TimeoutException) { }
    Check(fake.Plays == 0, "Stale audio played");
});
await Test("provider failure is reported and next dialogue recovers", async () =>
{
    var fake = new FakePlayer(); var pipeline = new DubbingPipeline(new FakeLocalizer(5), new FakeVoice(), fake, new(Path.Combine(testRoot, "recover")), new());
    await using var router = new DialogueRouter(pipeline, fake); var errors = 0; router.Error += _ => errors++;
    router.Submit(new("fail")); await Task.Delay(50); router.Submit(new("valid")); await Task.Delay(100);
    Check(errors == 1 && fake.Last == "valid", "Recovery failed");
});
await Test("automatic mode prefers subtitles and suppresses cross-source duplicate speech", () =>
{
    var sink = new CollectingSink(); var arbiter = new HybridArbiter(sink);
    arbiter.Subtitles.Submit(new("We need to get out!"));
    arbiter.Audio.Submit(new("we need to get out"));
    Check(sink.Lines.Count == 1, "Both paths spoke the same line");
    arbiter.Subtitles.Interrupt();
    arbiter.Subtitles.Submit(new("We need to get out!"));
    Check(sink.Lines.Count == 2, "A genuine subtitle repeat was blocked");
    return Task.CompletedTask;
});
await Test("pause and loading labels are not spoken", () =>
{
    var detector = new SubtitleChangeDetector(); detector.Observe("Paused", TimeSpan.Zero);
    Check(detector.Observe("Paused", TimeSpan.FromSeconds(1)).Event == SubtitleEvent.None, "Pause menu emitted speech"); return Task.CompletedTask;
});
await Test("sentence playback begins before later synthesis and cancellation discards partial cache", async () =>
{
    var player = new FakePlayer(); var voice = new ChunkedFakeVoice(true); var directory = Path.Combine(testRoot,"chunk-cancel");
    var pipeline = new DubbingPipeline(new FakeLocalizer(1), voice, player, new(directory), new(), 1000);
    using var cancellation = new CancellationTokenSource();
    var playing = pipeline.ProcessAsync(new("one. two."), cancellation.Token);
    for (var i=0;i<50 && player.Plays==0;i++) await Task.Delay(10);
    Check(player.Plays==1 && !playing.IsCompleted, "Did not begin playback before second synthesis completed");
    cancellation.Cancel(); try { await playing; } catch (OperationCanceledException) { }
    Check(!Directory.GetFiles(directory,"*.json").Any(), "Cached an interrupted partial line");
});
await Test("sentence order and complete generated-audio cache are preserved", async () =>
{
    var player = new FakePlayer(); var voice = new ChunkedFakeVoice(false); var cache = new DialogueCache(Path.Combine(testRoot,"chunks"));
    var pipeline = new DubbingPipeline(new FakeLocalizer(1), voice, player, cache, new(), 1000);
    AudioClip? saved = null; pipeline.Ready += (_,_,clip) => saved=clip;
    await pipeline.ProcessAsync(new("one. two."),default);
    Check(player.Plays==2 && player.Last=="two." && saved is not null && System.Text.Encoding.UTF8.GetString(saved.Bytes)=="one.two.", "Sentence order/combined clip changed");
    pipeline.Context.Clear(); var replay=await pipeline.ProcessAsync(new("one. two."),default);
    Check(replay.CacheHit && player.Plays==3, "Full combined clip was not replayed from cache");
});

await Test("ordered mode finishes lines, prefetches during speech, and never overlaps speakers", async () =>
{
    var output=new SequencePlayer(180); var voice=new ObservingVoice(()=>output.Active>0);
    var pipeline=new DubbingPipeline(new FakeLocalizer(20),voice,output,new(Path.Combine(testRoot,"ordered")),new());
    await using var router=new DialogueRouter(pipeline,output,true,3000);
    router.Submit(new("first")); await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    router.Submit(new("second")); await Task.Delay(30); router.Submit(new("third"));
    using var timeout=new CancellationTokenSource(3000); await router.WaitForIdleAsync(timeout.Token);
    Check(output.Lines.SequenceEqual(new[]{"first","second","third"}),"Ordered dialogue was cancelled or reordered");
    Check(output.MaxActive==1 && voice.PreparedDuringPlayback>0,"No preparation overlap, or simultaneous speech");
});
await Test("50 rapid submissions keep a bounded buffer and report every overload drop", async () =>
{
    var output=new SequencePlayer(120); var pipeline=new DubbingPipeline(new FakeLocalizer(20),new FakeVoice(),output,new(Path.Combine(testRoot,"flood")),new());
    using var trace=new PipelineTrace(Path.Combine(testRoot,"flood-trace")); pipeline.Trace=trace;
    await using var router=new DialogueRouter(pipeline,output,true,2000);
    router.Submit(new("first")); await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    for(var i=0;i<50;i++) { router.Submit(new("line"+i)); Check(router.PendingCount<=2,"Unbounded pending queue"); }
    using var timeout=new CancellationTokenSource(3000); await router.WaitForIdleAsync(timeout.Token);
    Check(output.Lines.Count+router.DroppedCount==51,"Missing terminal outcome for a submitted line");
    Check(router.DroppedCount>=46 && output.MaxActive==1,"Overload did not stay bounded");
});
await Test("ordered Stop cancels playback and prefetched work, then recovers", async () =>
{
    var output=new SequencePlayer(400); var pipeline=new DubbingPipeline(new FakeLocalizer(20),new FakeVoice(),output,new(Path.Combine(testRoot,"ordered-stop")),new());
    await using var router=new DialogueRouter(pipeline,output,true,2000);
    router.Submit(new("first")); await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)); router.Submit(new("prefetch"));
    await Task.Delay(50); var timer=Stopwatch.StartNew(); router.Interrupt();
    using var timeout=new CancellationTokenSource(3000); await router.WaitForIdleAsync(timeout.Token);
    Check(timer.ElapsedMilliseconds<500 && output.Lines.Count==1,"Stop allowed stale speech");
    router.Submit(new("recovery")); await router.WaitForIdleAsync(timeout.Token);
    Check(output.Lines.Last()=="recovery","Router did not recover after Stop");
});
await Test("a prefetched provider failure cannot let later playback overtake current speech", async () =>
{
    var output=new SequencePlayer(180); var pipeline=new DubbingPipeline(new FakeLocalizer(10),new FakeVoice(),output,new(Path.Combine(testRoot,"order-failure")),new());
    await using var router=new DialogueRouter(pipeline,output,true,2000);
    router.Submit(new("first")); await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)); router.Submit(new("fail")); router.Submit(new("third"));
    using var timeout=new CancellationTokenSource(3000); await router.WaitForIdleAsync(timeout.Token);
    Check(output.Lines.SequenceEqual(new[]{"first","third"}) && output.MaxActive==1,"Failure broke playback ordering");
});
await Test("deadline is checked after ordered playback wait, not only after synthesis", async () =>
{
    var output=new SequencePlayer(250); var pipeline=new DubbingPipeline(new FakeLocalizer(10),new FakeVoice(),output,new(Path.Combine(testRoot,"ordered-deadline")),new());
    await using var router=new DialogueRouter(pipeline,output,true,100);
    router.Submit(new("first")); await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)); router.Submit(new("expired"));
    using var timeout=new CancellationTokenSource(3000); await router.WaitForIdleAsync(timeout.Token);
    Check(output.Lines.Count==1 && router.DroppedCount==1,"Expired prefetched speech played late");
});
await Test("capture reference dimensions scale regions without changing the saved profile", () =>
{
    var p=new GameProfile { RegionReferenceWidth=1920,RegionReferenceHeight=1080 };
    var region=new CaptureRegion(300,900,1200,120); var result=SubtitleImages.Resolve(region,1280,720,p);
    Check(result==new CaptureRegion(200,600,800,80) && p.RegionReferenceWidth==1920,"Region scaling incorrect");
    return Task.CompletedTask;
});
await Test("diagnostic IDs and timestamps do not invalidate content caches", () =>
{
    var a=new Dialogue("Open this door."); var b=a with { EventId="other",FirstSeenAt=DateTimeOffset.UtcNow.AddHours(1) };
    Check(DialogueCache.Identity(a,new(),"mt","tts","voice",[])==DialogueCache.Identity(b,new(),"mt","tts","voice",[]),"Diagnostic metadata changed cache identity"); return Task.CompletedTask;
});
await Test("recorded OCR timing replays without a game and preserves duplicate filtering", async () =>
{
    var directory=Path.Combine(testRoot,"trace-replay");
    using(var trace=new PipelineTrace(directory))
    {
        trace.Write("ocr",null,new { text="Open the red door." }); await Task.Delay(170);
        trace.Write("ocr",null,new { text="Open the red door." }); await Task.Delay(20);
        trace.Write("ocr",null,new { text="Open the red door." });
    }
    var sink=new CollectingSink(); await DiagnosticReplay.ReplayOcrAsync(directory,sink,default);
    Check(sink.Lines.Count==1,"Trace replay missed/duplicated confirmed dialogue");
    try { DiagnosticReplay.CropPath(directory,"../other.png"); throw new Exception("Escaped replay directory"); } catch(InvalidDataException) { }
});

if(args.Contains("--rapid"))
{
    await Test("50-event wall-clock OCR replay, raw and enhanced",async ()=>
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await RapidAcceptance.RunAsync(Path.Combine(testRoot,"rapid"),timeout.Token);
        using var doc=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(testRoot,"rapid","rapid-Auto.json")));
        Check(doc.RootElement.GetProperty("recall").GetDouble()>=.95,"Enhanced OCR recall missed the 95% target; inspect rapid report");
    });
}
if(args.Contains("--speech-acceptance"))
{
    await Test("50 unseen rendered subtitles → native OCR → NLLB → repaired GPU Hindi → WASAPI",async ()=>
    {
        var directory=Path.Combine(testRoot,"speech-acceptance"); Directory.CreateDirectory(directory);
        using var mt=new LocalWorker("translate-nllb");using var gpu=new LocalWorker("tts-dml");using var cpu=new LocalWorker("tts");
        await Task.WhenAll(mt.WarmAsync(default),gpu.WarmAsync(default));
        using var player=new WasapiPlayer { Volume=.25f };
        using var trace=new PipelineTrace(Path.Combine(directory,"trace"));
        var voice=new CachedVoice(new FallbackVoice(new KokoroVoice(gpu),new KokoroVoice(cpu)),new DialogueCache(Path.Combine(directory,"cache")));
        var pipeline=new DubbingPipeline(new OfflineLocalizer(mt,1),voice,player,new(Path.Combine(directory,"dialogue-cache")),new() { DefaultVoice="hf_alpha" },12000) { Trace=trace };
        await using var router=new DialogueRouter(pipeline,player,true,12000);
        router.Error+=s=>Console.WriteLine("Speech acceptance: "+s);
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await RapidAcceptance.RunAsync(Path.Combine(directory,"replay"),timeout.Token,router,trace);
        await router.WaitForIdleAsync(timeout.Token);
        var metrics=pipeline.Metrics.Snapshot();
        Json.Write(Path.Combine(directory,"playback.json"),new { playbackStarts=metrics.Length,dropped=router.DroppedCount,pending=router.PendingCount,metrics,
            workerMiB=new { mt=mt.WorkingSetBytes/1048576.0,gpu=gpu.WorkingSetBytes/1048576.0,cpu=cpu.WorkingSetBytes/1048576.0 },
            note="Playback requests are real WASAPI starts, not a human audible-line count. First-observed latency and every drop reason are in trace. Not a GTA/FPS acceptance pass." });
        Console.WriteLine($"50-event speech acceptance: {metrics.Length} playback starts, {router.DroppedCount} drops. Detailed trace: {directory}");
        Check(router.PendingCount==0 && metrics.Length>0,"No real Hindi playback or backlog failed to drain");
    });
}

if (args.Contains("--integration"))
{
    await Test("cancelled local inference keeps its warm process and drains before the next reply", async () =>
    {
        using var worker = new LocalWorker("translate");
        await worker.WarmAsync(default); var pid = worker.ProcessId;
        using var cancel = new CancellationTokenSource();
        var pending = worker.RequestAsync(new { text = "The bridge is broken. Find another way." }, cancel.Token);
        cancel.Cancel(); var clock = Stopwatch.StartNew();
        try { using var ignored = await pending; throw new Exception("Cancelled request returned data"); }
        catch (OperationCanceledException) { }
        var cancellationMs = clock.Elapsed.TotalMilliseconds;
        Check(cancellationMs < 500, "Cancellation waited for inference");
        using var reply = await worker.RequestAsync(new { text = "Open the door." }, default);
        Check(worker.ProcessId == pid, "Cancellation restarted the model process");
        var hindi = reply.RootElement.GetProperty("hindi").GetString() ?? "";
        Check(hindi.Contains("दरवाज") && !hindi.Contains("पुल"), "Cancelled reply leaked into next request: " + hindi);
        Json.Write(Path.Combine(testRoot,"warm-cancellation.json"), new { sameProcess = true, cancellationMs, nextReplyTotalMs = clock.Elapsed.TotalMilliseconds, hindi });
    });
    await Test("real Windows OCR recognizes a rendered English subtitle", async () =>
    {
        using var bitmap = new Bitmap(1000, 110);
        using (var g = Graphics.FromImage(bitmap)) { g.Clear(Color.Black); using var font = new Font("Segoe UI", 30); g.DrawString("We gotta get out of here!", font, Brushes.White, 20, 20); }
        var result = await new WindowsSubtitleOcr().RecognizeAsync(bitmap, default);
        Check(result.Contains("get out", StringComparison.OrdinalIgnoreCase), "OCR returned: " + result);
        File.WriteAllText(Path.Combine(testRoot, "ocr-output.txt"), result);
    });
    await Test("real offline model translates an unseen line", async () =>
    {
        using var worker = new LocalWorker("translate");
        var result = await new OfflineLocalizer(worker).TranslateAsync(new("The bridge is broken. Find another way."), new(), [], default);
        Check(result.Hindi.Contains("पुल") && result.Source != "curated-dialogue-override", "Did not run actual MT");
        Json.Write(Path.Combine(testRoot, "unseen-translation.json"), result);
    });
    await Test("real offline glossary keeps a configured character name", async () =>
    {
        using var worker = new LocalWorker("translate");
        var profile = new GameProfile(); profile.Glossary["Arthur"] = "ऑर्थर";
        var result = await new OfflineLocalizer(worker).TranslateAsync(new("Arthur, open the door."), profile, [], default);
        Check(result.Hindi.Contains("ऑर्थर") && result.Hindi.Contains("दरवाजा"), "Glossary replacement failed: " + result.Hindi);
    });
    await Test("real Hindi WAV synthesis and actual WASAPI device playback", async () =>
    {
        var sample = Json.Samples()[1];
        var line = await new FixtureLocalizer().TranslateAsync(new(sample.English), new(), [], default);
        var clip = await new EspeakVoice().SynthesizeAsync(line, "hi", default);
        Check(clip.Bytes.Length > 10000, "Empty WAV");
        WasapiPlayer.SaveWave(clip, Path.Combine(testRoot, "hindi-espeak.wav"));
        using var player = new WasapiPlayer { Volume = .35f }; var started = false;
        await player.PlayAsync(clip, () => started = true, default); Check(started, "Playback never started");
    });
    await Test("real playback cancellation responds without waiting for clip end", async () =>
    {
        var clip = await new EspeakVoice().SynthesizeAsync(new("हमें यहाँ से निकलना होगा! हमें यहाँ से निकलना होगा!", "neutral", "test"), "hi", default);
        using var player = new WasapiPlayer { Volume = .25f }; using var cancel = new CancellationTokenSource(100);
        var timer = Stopwatch.StartNew();
        try { await player.PlayAsync(clip, () => { }, cancel.Token); } catch (OperationCanceledException) { }
        Check(timer.ElapsedMilliseconds < 1000, "Cancellation waited for playback");
    });
}
if (args.Contains("--process-audio"))
{
    await Test("real per-process loopback captures only a synthetic child", async () =>
    {
        var releasePath = Path.Combine(testRoot, "audio-source.release");
        using var child = Process.Start(new ProcessStartInfo(sourceExecutable) { ArgumentList = { releasePath }, UseShellExecute = false, CreateNoWindow = true })!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var capture = new ProcessAudioCapture(); long bytes = 0; double energy = 0; var peak = 0;
        capture.DataAvailable += data => { Interlocked.Add(ref bytes, data.Length); for (var i = 0; i+1 < data.Length; i+=2) { var amplitude = Math.Abs((int)BitConverter.ToInt16(data, i)); energy += amplitude; peak = Math.Max(peak, amplitude); } };
        var running = capture.RunAsync(child.Id, timeout.Token);
        try
        {
            using (var parentOutput = new WasapiPlayer { Volume = .05f })
            {
                var tone = new byte[16000];
                for (var i = 0; i < 8000; i++) { var value = (short)(Math.Sin(i * 2 * Math.PI * 440 / 16000) * 5000); tone[2*i]=(byte)value; tone[2*i+1]=(byte)(value>>8); }
                await parentOutput.PlayAsync(new(tone,"pcm_16000","synthetic-parent-tone"), () => { }, default);
            }
            // Windows float→PCM16 conversion can dither digital silence by one LSB. The parent
            // tone peaks near 250 LSB; allow <=2 LSB, not an invalid bit-exact-zero assumption.
            Check(peak <= 2, $"Captured parent playback; process isolation failed (peak={peak}, bytes={bytes}, energy={energy})");
            File.WriteAllText(releasePath, "start synthetic speech");
            await Task.Delay(8000); timeout.Cancel();
            try { await running; } catch (OperationCanceledException) { }
            Check(bytes > 16000 && energy > 100000, $"No real selected-process audio: bytes={bytes}, energy={energy}");
            Json.Write(Path.Combine(testRoot, "process-capture.json"), new { bytes, sumAbsoluteAmplitude = energy, rawAudioSaved = false });
        }
        finally { timeout.Cancel(); if (!child.HasExited) child.Kill(true); }
    });
}
if (args.Contains("--screen"))
{
    await Test("actual window crop → OCR → MT → Hindi TTS → WASAPI", async () =>
    {
        using var mt = new LocalWorker("translate"); using var neural = new LocalWorker("tts"); using var player = new WasapiPlayer { Volume = .25f };
        IVoiceProvider voice = args.Contains("--neural") ? new KokoroVoice(neural) : new EspeakVoice();
        await mt.WarmAsync(default); if (args.Contains("--neural")) await neural.WarmAsync(default);
        using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "--subtitle-source-child" }, UseShellExecute = false })!;
        try
        {
            for (var i = 0; i < 30; i++) { child.Refresh(); if (child.MainWindowHandle != 0) break; await Task.Delay(100); }
            Check(child.MainWindowHandle != 0, "Synthetic fixture did not open");
            var pipeline = new DubbingPipeline(new OfflineLocalizer(mt), voice, player, new(Path.Combine(testRoot, "screen-cache")), new() { DefaultVoice = "hf_alpha" }, 10000);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var count = 0;
            pipeline.Status += s => Console.WriteLine("Screen pipeline: " + s);
            pipeline.Ready += (_, _, _) => { if (Interlocked.Increment(ref count) >= 2) ready.TrySetResult(); };
            await using var router = new DialogueRouter(pipeline, player); router.Error += e => Console.WriteLine("Screen fixture status: " + e);
            var game = new GameWindow(child.Id, child.MainWindowHandle, "synthetic", "Synthetic subtitle source");
            var bounds = GameWindows.Bounds(game.Handle); GameWindows.SetForegroundWindow(game.Handle);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55));
            var monitor = new SubtitleMonitor();
            monitor.Status += s => Console.WriteLine("Screen fixture: " + s);
            var watching = Task.Run(() => monitor.RunAsync(game, new(0,0,bounds.Width,bounds.Height), router, timeout.Token));
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(50)); await Task.Delay(2500); timeout.Cancel();
            try { await watching; } catch (OperationCanceledException) { }
            Check(count == 2, $"Duplicate/missing fixture lines: {count}");
            Check(pipeline.Metrics.Snapshot().Length == 2, "Did not request actual playback for both lines");
            Json.Write(Path.Combine(testRoot, "screen-latency.json"), pipeline.Metrics.Snapshot());
        }
        finally { if (!child.HasExited) child.Kill(true); }
    });
}
if (args.Contains("--neural"))
{
    await Test("real neural Hindi synthesis, playback and context-safe cache replay", async () =>
    {
        using var mt = new LocalWorker("translate"); using var tts = new LocalWorker("tts");
        await Task.WhenAll(mt.WarmAsync(default), tts.WarmAsync(default));
        using var player = new WasapiPlayer { Volume = .3f };
        var pipeline = new DubbingPipeline(new OfflineLocalizer(mt), new KokoroVoice(tts), player, new(Path.Combine(testRoot, "neural-cache")), new() { DefaultVoice = "hm_omega" }, 15000);
        var first = await pipeline.ProcessAsync(new("The bridge is broken. Find another way."), default);
        pipeline.Context.Clear();
        var replay = await pipeline.ProcessAsync(new("The bridge is broken. Find another way."), default);
        Check(replay.CacheHit && replay.FirstAudioMs < first.FirstAudioMs, "Cached audio not reused");
        Json.Write(Path.Combine(testRoot, "neural-latency.json"), new[] { first, replay });
        Json.Write(Path.Combine(testRoot, "worker-memory.json"), new { ttsMiB = tts.WorkingSetBytes/1048576.0, translationMiB = mt.WorkingSetBytes/1048576.0, testHostMiB = Process.GetCurrentProcess().WorkingSet64/1048576.0 });
    });
}
if (args.Contains("--asr"))
{
    await Test("per-process synthetic English → local ASR → Hindi neural playback", async () =>
    {
        using var asr = new LocalWorker("asr"); using var mt = new LocalWorker("translate"); using var tts = new LocalWorker("tts");
        await Task.WhenAll(asr.WarmAsync(default), mt.WarmAsync(default), tts.WarmAsync(default));
        using var child = Process.Start(new ProcessStartInfo(sourceExecutable) { UseShellExecute = false, CreateNoWindow = true })!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var capture = new ProcessAudioCapture();
        var queue = System.Threading.Channels.Channel.CreateBounded<byte[]>(100);
        capture.DataAvailable += data => queue.Writer.TryWrite(data);
        var capturing = capture.RunAsync(child.Id, timeout.Token);
        var recognized = "";
        try
        {
            await foreach (var pcm in queue.Reader.ReadAllAsync(timeout.Token))
            {
                using var response = await asr.RequestAsync(new { pcm = Convert.ToBase64String(pcm) }, timeout.Token);
                var text = response.RootElement.GetProperty("text").GetString() ?? "";
                if (text.Length > 5) { recognized = text; break; }
            }
            Check(recognized.Contains("bridge") || recognized.Contains("way"), "Unexpected recognition: " + recognized);
            timeout.Cancel(); try { await capturing; } catch (OperationCanceledException) { }
            using var player = new WasapiPlayer { Volume = .25f };
            var pipeline = new DubbingPipeline(new OfflineLocalizer(mt), new KokoroVoice(tts), player, new(Path.Combine(testRoot, "asr-cache")), new() { DefaultVoice = "hf_alpha" }, 15000);
            var record = await pipeline.ProcessAsync(new(recognized), default);
            Json.Write(Path.Combine(testRoot, "asr-pipeline.json"), new { recognized, record, rawAudioSaved = false });
        }
        finally { timeout.Cancel(); try { await capturing; } catch (OperationCanceledException) { } if (!child.HasExited) child.Kill(true); }
    });
}
if (args.Contains("--duck"))
{
    await Test("selected-process ducking restores original session volume after cancellation", async () =>
    {
        using var child = Process.Start(new ProcessStartInfo(sourceExecutable) { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            await Task.Delay(4200);
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
            NAudio.CoreAudioApi.AudioSessionControl? target = null;
            var sessions = device.AudioSessionManager.Sessions;
            for (var i=0;i<sessions.Count;i++) { var item=sessions[i]; if (item.GetProcessID == child.Id) { target=item; break; } item.Dispose(); }
            Check(target is not null, "Synthetic child has no audio session");
            using (target!)
            {
                var original = target!.SimpleAudioVolume.Volume;
                var fake = new HoldingPlayer(); var duck = new GameDuckingPlayer(fake) { ProcessId = child.Id, Enabled = true, Level = .3f };
                using var cancel = new CancellationTokenSource();
                var playing = duck.PlayAsync(new([],"fake","test"), () => { }, cancel.Token);
                var firstTask = await Task.WhenAny(fake.Started.Task, playing).WaitAsync(TimeSpan.FromSeconds(2));
                if (firstTask == playing) await playing;
                Check(Math.Abs(target.SimpleAudioVolume.Volume - original*.3f) < .01, "Game volume was not ducked");
                cancel.Cancel(); try { await playing; } catch (OperationCanceledException) { }
                Check(Math.Abs(target.SimpleAudioVolume.Volume - original) < .01, "Original game volume was not restored");
            }
        }
        finally { if (!child.HasExited) child.Kill(true); }
    });
}
Json.Write(Path.Combine(testRoot, "results.json"), results);
Console.WriteLine($"{results.Count-failed}/{results.Count} passed; results: {testRoot}");
return failed == 0 ? 0 : 1;

sealed class FakeLocalizer(int delay) : ILocalizer
{
    public string Identity => "fake-localizer";
    public async Task<LocalizedLine> TranslateAsync(Dialogue line, GameProfile p, IReadOnlyList<string> h, CancellationToken t)
    { await Task.Delay(delay, t); if (line.English == "fail") throw new IOException("Synthetic provider failure"); return new(line.English, "neutral", "fake"); }
}
sealed class FakeVoice : IVoiceProvider
{
    public string Identity => "fake-voice";
    public Task<AudioClip> SynthesizeAsync(LocalizedLine l, string v, CancellationToken t) => Task.FromResult(new AudioClip(System.Text.Encoding.UTF8.GetBytes(l.Hindi), "fake", "fake"));
}
sealed class FakePlayer : IAudioPlayer
{
    public int Plays; public string? Last;
    public Task PlayAsync(AudioClip clip, Action started, CancellationToken t) { t.ThrowIfCancellationRequested(); Last = System.Text.Encoding.UTF8.GetString(clip.Bytes); Interlocked.Increment(ref Plays); started(); return Task.CompletedTask; }
    public void Stop() { }
}
sealed class CollectingSink : IDialogueSink
{
    public List<Dialogue> Lines = new();
    public void Submit(Dialogue line) => Lines.Add(line);
    public void Interrupt() { }
}
sealed class HoldingPlayer : IAudioPlayer
{
    public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task PlayAsync(AudioClip c, Action started, CancellationToken token) { started(); Started.TrySetResult(); await Task.Delay(5000, token); }
    public void Stop() { }
}
sealed class ChunkedFakeVoice(bool blockSecond) : IChunkedVoiceProvider
{
    private int calls;
    public string Identity => "synthetic-chunk-test";
    public IReadOnlyList<LocalizedLine> DeliveryChunks(LocalizedLine line) => line.Hindi.Split(' ').Select(s=>line with { Hindi=s }).ToArray();
    public async Task<AudioClip> SynthesizeAsync(LocalizedLine line,string voice,CancellationToken token)
    { var call=Interlocked.Increment(ref calls); await Task.Delay(blockSecond && call==2 ? 10000 : 5,token); return new(System.Text.Encoding.UTF8.GetBytes(line.Hindi),"pcm_24000",Identity); }
}
sealed class PassiveTestWindow : System.Windows.Forms.Form
{
    protected override bool ShowWithoutActivation => true;
}
sealed class SequencePlayer(int durationMs) : IAudioPlayer
{
    public readonly List<string> Lines=new();
    public readonly TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Active,MaxActive;
    public async Task PlayAsync(AudioClip clip,Action started,CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var active=Interlocked.Increment(ref Active); MaxActive=Math.Max(MaxActive,active);
        lock(Lines) Lines.Add(System.Text.Encoding.UTF8.GetString(clip.Bytes));
        started(); Started.TrySetResult();
        try { await Task.Delay(durationMs,token); } finally { Interlocked.Decrement(ref Active); }
    }
    public void Stop() { }
}
sealed class ObservingVoice(Func<bool> playing) : IVoiceProvider
{
    public int PreparedDuringPlayback;
    public string Identity=>"observed-test-voice";
    public async Task<AudioClip> SynthesizeAsync(LocalizedLine line,string voice,CancellationToken token)
    {
        if(playing())Interlocked.Increment(ref PreparedDuringPlayback);
        await Task.Delay(20,token); return new(System.Text.Encoding.UTF8.GetBytes(line.Hindi),"pcm_24000",Identity);
    }
}
