using GameDub.Core;

internal static class DialogueRepairTests
{
    private static void Check(bool value,string message) { if(!value)throw new Exception(message); }
    public static async Task Run(string root,Func<string,Func<Task>,Task> test)
    {
        await test("conversational Hindi keeps negation, numbers, names, and reviewed terms",()=>
        {
            var profile=new GameProfile { Glossary=new() { ["Help"]="सहायता" } };
            var hindi=ConversationalHindi.Adapt("कृपया सहायता करो। मुझे सहायता की आवश्यकता नहीं है। वास्तव में, 25 लोग कार्यालय में प्रतीक्षा कर रहे हैं।",profile);
            Check(hindi.Contains("प्लीज़")&&hindi.Contains("ज़रूरत नहीं है")&&hindi.Contains("25")&&hindi.Contains("ऑफ़िस")&&hindi.Contains("इंतज़ार"),"Register edit lost facts or did not simplify wording: "+hindi);
            Check(hindi.Contains("सहायता"),"User glossary was rewritten");
            profile.Glossary.Clear();Check(ConversationalHindi.Adapt("मैंने प्रयास किया, लेकिन वह कदापि नहीं आया।",profile)=="मैंने कोशिश की, लेकिन वह बिल्कुल नहीं आया।","Grammar or negation changed");
            profile.ConversationalHindi=false;Check(ConversationalHindi.Adapt("कृपया प्रतीक्षा करें।",profile)=="कृपया प्रतीक्षा करें।","Style opt-out ignored");
            return Task.CompletedTask;
        });
        await test("conversational meaning guard rejects lost negation, numbers, Latin leaks and mixed address forms",()=>
        {
            Check(ConversationalLocalizer.Validate("Don't touch anything.","कुछ छूना।") is not null,"Lost negation accepted");
            Check(ConversationalLocalizer.Validate("Don't touch anything.","कुछ भी मत छूना।") is null,"Valid negation rejected");
            Check(ConversationalLocalizer.Validate("We split it 60-40 at 9.","हम 9 बजे बाँटेंगे।") is not null,"Lost number accepted");
            Check(ConversationalLocalizer.Validate("Who sold us out?","किसने हमें बे sold किया?") is not null,"Latin leak accepted");
            Check(ConversationalLocalizer.CheckRegister("तू मेरी बात सुन, तुम्हारा क्या जाता है?",null) is not null,"Mixed तू/तुम accepted");
            Check(ConversationalLocalizer.CheckRegister("तू मेरी बात सुन, तेरा क्या जाता है?","tum") is null,"Consistent line rejected");
            Check(ConversationalLocalizer.Clean("Hindi: \"चलो निकलते हैं।\"\n(note)")=="चलो निकलते हैं।","Commentary not stripped");
            var profile=new GameProfile();
            Check(ConversationalHindi.Adapt("हमें प्रतीक्षा करनी होगी।",profile)=="हमें प्रतीक्षा करनी होगी।","Gender-changing swap broke grammar");
            return Task.CompletedTask;
        });
        await test("speaker gender reaches the localiser only from the character voice mapping",()=>
        {
            var profile=new GameProfile { GameId="GTA5_Enhanced",Voices=new() { ["Amanda"]="hf_beta" } };
            var localizer=new ConversationalLocalizer(new LlamaServer(),new FixtureLocalizer());
            Check(localizer.BuildPrompt(new("I'm done.","Amanda"),profile,[]).Contains("female"),"Mapped female speaker not passed");
            Check(!localizer.BuildPrompt(new("I'm done.","Unknown"),profile,[]).Contains("male"),"Gender guessed for unknown speaker");
            return Task.CompletedTask;
        });
        await test("GPU guard: released natural voice never loads, CPU voice speaks instead",async()=>
        {
            GpuGuard.Shed(true,false,"test: game filled video memory");
            try
            {
                using var worker=new LocalWorker("tts-f5");
                var fallbackUsed=false;var notified="";
                var voice=new FallbackVoice(new NaturalVoice(worker),new StubVoice(()=>fallbackUsed=true),m=>notified=m);
                var clip=await voice.SynthesizeAsync(new("ठीक है।","neutral","test"),"hm_omega",default);
                Check(fallbackUsed&&clip.Bytes.Length>0,"Fallback voice did not speak");
                Check(worker.ProcessId is null,"Natural voice process was started while the GPU was reserved for the game");
                Check(notified.Contains("video memory"),"User was not told why the voice changed: "+notified);
            }
            finally { GpuGuard.Reset(); }
        });
        await test("memory guard: models load only if the game keeps its system memory and video memory",()=>
        {
            var (vram,mem,running,reserve)=(GpuGuard.VramProbe,GpuGuard.MemoryProbe,GpuGuard.GameRunning,GpuGuard.CommitReserveMiB);
            try
            {
                GpuGuard.CommitReserveMiB=3072;GpuGuard.VramProbe=()=>(2000,16000);
                GpuGuard.MemoryProbe=()=>new(5000,22800,2000);GpuGuard.GameRunning=()=>true;
                Check(!GpuGuard.CanLoad(GpuGuard.LlmMiB,GpuGuard.LlmCommitMiB,out var why)&&why.Contains("system memory"),"LLM loaded into a 5 GB commit headroom: "+why);
                GpuGuard.MemoryProbe=()=>new(12000,38000,6000);
                Check(GpuGuard.CanLoad(GpuGuard.LlmMiB,GpuGuard.LlmCommitMiB,out why),"Refused with plenty of memory: "+why);
                GpuGuard.GameRunning=()=>false;
                Check(!GpuGuard.CanLoad(GpuGuard.NaturalVoiceMiB,GpuGuard.NaturalVoiceCommitMiB,out why)&&why.Contains("game to start"),"No room kept for the game to launch: "+why);
                GpuGuard.GameRunning=()=>true;GpuGuard.VramProbe=()=>(14500,16000);
                Check(!GpuGuard.CanLoad(GpuGuard.NaturalVoiceMiB,GpuGuard.NaturalVoiceCommitMiB,out why)&&why.Contains("video memory"),"Loaded into full VRAM: "+why);
                // While playing: low commit releases the voice first, then the LLM on the next check.
                GpuGuard.VramProbe=()=>(6000,16000);GpuGuard.MemoryProbe=()=>new(1200,22800,900);
                var released=new List<string>();using var watch=new GpuWatch(_=>released.Add("voice"),_=>released.Add("llm"),0);
                watch.Check();Check(released.SequenceEqual(["voice"])&&GpuGuard.VoiceShed&&!GpuGuard.LlmShed,"Voice not released first: "+string.Join(",",released));
                watch.Check();Check(released.SequenceEqual(["voice","llm"])&&GpuGuard.LlmShed,"LLM kept while memory still short");
                Check(!GpuGuard.TryRestore(out _),"Restored while memory is still short");
                GpuGuard.MemoryProbe=()=>new(14000,38000,6000);
                Check(GpuGuard.TryRestore(out why)&&!GpuGuard.VoiceShed&&!GpuGuard.LlmShed,"Not restored once memory is free: "+why);
            }
            finally { (GpuGuard.VramProbe,GpuGuard.MemoryProbe,GpuGuard.GameRunning,GpuGuard.CommitReserveMiB)=(vram,mem,running,reserve);GpuGuard.Reset(); }
            return Task.CompletedTask;
        });
        await test("long Hindi delivery retains every word in bounded clauses",()=>
        {
            var text="हमें स्टेशन पहुँचने से पहले माइकल से बात करनी होगी, क्योंकि पुल बंद है और दूसरी सड़क पर पुलिस हमारा इंतज़ार कर रही है। "+
                string.Join(" ",Enumerable.Repeat("किसी को पीछे मत छोड़ना और जब तक मैं न कहूँ तब तक दरवाज़ा मत खोलना",8))+"।";
            var chunks=SpeechDelivery.Chunks(new(text,"neutral","test"));
            Check(chunks.Count>3&&chunks.All(c=>c.Hindi.Length<=140),"Long line was not bounded");
            Check(Text.Clean(string.Join(" ",chunks.Select(c=>c.Hindi)))==Text.Clean(text),"Speech splitting lost or duplicated content");
            return Task.CompletedTask;
        });
        await test("GTA clipped two-row crop is expanded once without losing its lower edge",()=>
        {
            var p=new GameProfile { GameId="GTA5_Enhanced",Region=new(341,1007,1314,72),RegionReferenceWidth=1920,RegionReferenceHeight=1080 };
            Check(SubtitleImages.RepairClippedGameRegion(p)&&p.Region!.Y<950&&p.Region.Height>=160&&p.Region.Y+p.Region.Height==1079,"Still clipping upper subtitle row");
            var region=p.Region;Check(!SubtitleImages.RepairClippedGameRegion(p)&&p.Region==region,"Repeatedly enlarged crop");
            var custom=new GameProfile { GameId="CustomGame",Region=new(0,800,1000,72),RegionReferenceHeight=1080 };
            SubtitleImages.RepairClippedGameRegion(custom);Check(custom.Region!.Height==72,"Altered an unrelated game's custom crop");return Task.CompletedTask;
        });
        await test("GTA pause controls are suppressed while ordinary dialogue about accounts remains",()=>
        {
            foreach(var text in new[]{"Browse Rockstar Games Account Caps Hide Menu Back","LS / South Rockford DR Zoom Page Down Page Select Point of Interest Blip Tab Back Esc"})
            { var detector=new SubtitleChangeDetector();detector.Observe(text,TimeSpan.Zero);Check(detector.Observe(text,TimeSpan.FromSeconds(1)).Event!=SubtitleEvent.Line,"Menu controls were spoken"); }
            Check(!SubtitleUiFilter.IsControlText("I lost my Rockstar Games account. Please help me get it back."),"Rejected genuine dialogue");return Task.CompletedTask;
        });
        if(Environment.GetCommandLineArgs().Contains("--long-dialogue"))
        {
            await test("real offline long paragraph translation preserves late names and numbers",async()=>
            {
                var english="My practice is closed now, but you can tune into my new radio show for all your mental health needs. "+
                    "Please wait in the office. We must not leave Michael behind. Franklin has 25 dollars, and Amanda will meet us at 9. "+
                    string.Join(" ",Enumerable.Repeat("The bridge is closed, so we need to follow the river and take the next road without waking the guards.",8))+" The final code is 7429.";
                using var worker=new LocalWorker("translate");using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(55));
                var localizer=new OfflineLocalizer(worker);var translated=await localizer.TranslateAsync(new(english),new(),[],limit.Token);
                Check(translated.Hindi.Contains("7429"),"Lost the final sentence of a long paragraph");
                Check(translated.Hindi.Length>500&&translated.Hindi.Contains("25"),"Long paragraph was truncated");
                Json.Write(Path.Combine(root,"long-dialogue-translation.json"),new { english,hindi=translated.Hindi,note="Actual model output; numeric coverage checks are not a human meaning-accuracy score." });
            });
        }
    }
    private sealed class StubVoice(Action used) : IVoiceProvider
    {
        public string Identity=>"stub-cpu-voice";
        public Task<AudioClip> SynthesizeAsync(LocalizedLine line,string voice,CancellationToken token) { used();return Task.FromResult(new AudioClip(new byte[480],"pcm_24000",Identity)); }
    }
}
