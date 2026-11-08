using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GameDub.Core;

namespace GameDub.App;

public sealed record CharacterRow(string Name,string Voice,string Category);
public sealed record VoiceSampleChoice(string Path,string Label);
public partial class MainWindow
{
    private DialogueEventStore? eventStore;
    private readonly DispatcherTimer historyWatch=new() { Interval=TimeSpan.FromMilliseconds(400) };
    private bool refreshingCharacters;
    private string characterNames="";
    private long historyRevision=-1;
    private void OpenDialogueHistory()
    {
        eventStore?.Dispose();
        var id=string.Concat(profile.GameId.Where(c=>char.IsLetterOrDigit(c)||c is '-' or '_'));
        eventStore=new(Path.Combine(Paths.Root,"dialogue-history",id+".sqlite"));
        historyRevision=-1;
    }
    private void InitializeV2Ui()
    {
        SelectCombo(PolicyMode,settings.DialoguePolicy);
        CharacterVoice.ItemsSource=CharacterVoices.Catalog;CharacterVoice.SelectedIndex=0;
        FallbackCharacterVoice.ItemsSource=CharacterVoices.Catalog;
        RefreshCharacters();
        RefreshBenchmarks();
        historyWatch.Tick+=(_,_)=>
        {
            if(eventStore is null)return;
            if(historyRevision!=eventStore.Revision)
            {
                historyRevision=eventStore.Revision;
                var selected=(DialogueHistory.SelectedItem as DialogueEvent)?.Id;
                var rows=eventStore.Recent();DialogueHistory.ItemsSource=rows;
                var names=KnownCharacterNames();if(string.Join('\n',names)!=characterNames)RefreshCharacters();
                if(selected is not null)DialogueHistory.SelectedItem=rows.FirstOrDefault(e=>e.Id==selected);
            }
            var current=router as StoryDialogueScheduler;
            BacklogStatus.Text=current is null?"Stopped • dialogue remains in History for replay":$"{settings.DialoguePolicy} • {current.PendingCount} waiting • {current.PreparedCount}/3 prepared buffers • {current.DroppedCount} unspoken"+(current.NeedsCatchup?" • Catch up needed; all text is retained.":"");
            if(DialogueHistory.SelectedItem is DialogueEvent item)HistoryDetail.Text=$"{item.SpeakerEvidence} • voice {item.VoiceId} • translation {item.TranslationState} • synthesis {item.SynthesisState}\n{item.CancellationReason??item.Error}";
        };
        historyWatch.Start();
    }
    private void RefreshCharacters()
    {
        if(CharacterTable is null)return;refreshingCharacters=true;
        var names=KnownCharacterNames();characterNames=string.Join('\n',names);
        CharacterTable.ItemsSource=names.Select(name=> { var id=profile.VoiceFor(name);var v=CharacterVoices.Catalog.FirstOrDefault(x=>x.Id==id);return new CharacterRow(name,v?.Label??id,v?.Category??"Unspecified"); }).ToArray();
        ManualCharacter.ItemsSource=new[]{"Unknown"}.Concat(names).ToArray();ManualCharacter.SelectedItem=profile.ManualSpeaker;
        if(ManualCharacter.SelectedIndex<0)ManualCharacter.SelectedIndex=0;
        FallbackCharacterVoice.SelectedItem=CharacterVoices.Catalog.FirstOrDefault(v=>v.Id==profile.DefaultVoice)??CharacterVoices.Catalog[0];
        refreshingCharacters=false;
    }
    private string[] KnownCharacterNames()=>profile.Voices.Keys.Concat(profile.Characters.Keys)
        .Concat(eventStore?.Recent(500).Select(e=>e.Speaker)??[])
        .Where(n=>!n.Equals("Unknown",StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
    private void RefreshBenchmarks()
    {
        var directory=Path.Combine(Paths.Workspace,"artifacts","v2-voices");
        BenchmarkSamples.ItemsSource=Directory.Exists(directory)?Directory.GetFiles(directory,"*.wav").Order().Select(p=>new VoiceSampleChoice(p,Path.GetFileNameWithoutExtension(p))).ToArray():[];
        BenchmarkSamples.SelectedIndex=0;
    }
    private void RefreshBenchmarks_Click(object sender,RoutedEventArgs e)=>RefreshBenchmarks();
    private async void PlayBenchmark_Click(object sender,RoutedEventArgs e)
    {
        if(BenchmarkSamples.SelectedItem is not VoiceSampleChoice choice)return;
        await StopAllAsync();ReadSettings();var run=operation=new CancellationTokenSource();
        try
        {
            var data=await File.ReadAllBytesAsync(choice.Path,run.Token);if(data.Length>8*1024*1024)throw new InvalidDataException("Comparison sample too large");
            await audio.PlayAsync(new(data,"wav",choice.Label),()=>SetStatus("Listening comparison: "+choice.Label),run.Token);
        }
        catch(Exception error) { Fail(error); }
        finally { if(operation==run)operation=null;run.Dispose(); }
    }
    private void SaveVoiceProfile()
    {
        var id=string.Concat(profile.GameId.Where(c=>char.IsLetterOrDigit(c)||c is '-' or '_'));
        Json.Write(Path.Combine(Paths.Profiles,id+".json"),profile);ProfileJson.Text=JsonSerializer.Serialize(profile,Json.Options);
    }
    private void ManualCharacter_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(!initialized||refreshingCharacters||ManualCharacter.SelectedItem is not string name)return;
        lock(profile)profile.ManualSpeaker=name;SaveVoiceProfile();
        SetStatus(name=="Unknown"?"Unlabeled dialogue uses your stable fallback voice; no identity is inferred.":$"Manual speaker: {name}. Explicit subtitle labels still take priority.");
    }
    private void FallbackCharacterVoice_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(!initialized||refreshingCharacters||FallbackCharacterVoice.SelectedItem is not HindiVoice voice)return;
        lock(profile)profile.DefaultVoice=voice.Id;SaveVoiceProfile();SetStatus($"Unknown speaker fallback: {voice.Label}");
    }
    private void CharacterTable_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(CharacterTable.SelectedItem is not CharacterRow row)return;
        CharacterName.Text=row.Name;CharacterVoice.SelectedItem=CharacterVoices.Catalog.FirstOrDefault(v=>v.Id==profile.VoiceFor(row.Name));
    }
    private void SaveCharacter_Click(object sender,RoutedEventArgs e)
    {
        var name=CharacterName.Text.Trim();if(name.Length is <1 or >40||CharacterVoice.SelectedItem is not HindiVoice voice) { SetStatus("Enter a character label and select a Hindi voice.");return; }
        lock(profile)
        {
            if(CharacterTable.SelectedItem is CharacterRow old)
            {
                foreach(var key in profile.Voices.Keys.Where(k=>k.Equals(old.Name,StringComparison.OrdinalIgnoreCase)).ToArray())profile.Voices.Remove(key);
                foreach(var key in profile.Characters.Keys.Where(k=>k.Equals(old.Name,StringComparison.OrdinalIgnoreCase)).ToArray())profile.Characters.Remove(key);
                if(profile.ManualSpeaker.Equals(old.Name,StringComparison.OrdinalIgnoreCase))profile.ManualSpeaker=name;
            }
            foreach(var key in profile.Voices.Keys.Where(k=>k.Equals(name,StringComparison.OrdinalIgnoreCase)).ToArray())profile.Voices.Remove(key);
            foreach(var key in profile.Characters.Keys.Where(k=>k.Equals(name,StringComparison.OrdinalIgnoreCase)).ToArray())profile.Characters.Remove(key);
            profile.Voices[name]=voice.Id;profile.Characters[name]=new(voice.Id,voice.Category,voice.Timbre);
        }
        SaveVoiceProfile();RefreshCharacters();SetStatus($"Saved {name} → {voice.Label}. Applies to new dialogue and replay; current speech finishes.");
    }
    private void ResetCharacters_Click(object sender,RoutedEventArgs e)
    {
        if(MessageBox.Show(this,"Reset this game's speaker mappings? The fallback voice and dialogue history are preserved.","Reset character voices",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
        lock(profile) { profile.Voices.Clear();profile.Characters.Clear();profile.ManualSpeaker="Unknown"; }
        SaveVoiceProfile();RefreshCharacters();
    }
    private async void PreviewCharacter_Click(object sender,RoutedEventArgs e)
    {
        if(CharacterVoice.SelectedItem is not HindiVoice voice)return;
        await StopAllAsync();ReadSettings();var run=operation=new CancellationTokenSource();
        try
        {
            mixer.ProcessId=0;
            var provider=Providers.Voice(settings,voiceWorker,gpuVoiceWorker,SetStatus);
            var clip=await provider.SynthesizeAsync(new("तुम आ गए। मुझे लगा था अब हम कभी नहीं मिलेंगे।","neutral","voice-preview"),voice.Id,run.Token);
            await audio.PlayAsync(clip,()=>SetStatus($"Previewing {voice.Label}; no emotion control is claimed."),run.Token);
        }
        catch(Exception error) { Fail(error); }
        finally { if(operation==run)operation=null;run.Dispose(); }
    }
    private void CatchUp_Click(object sender,RoutedEventArgs e)
    {
        if(router is StoryDialogueScheduler controller)controller.CatchUp();else SetStatus("Start dubbing first, or select a History line to replay.");
    }
    private void ClearScene_Click(object sender,RoutedEventArgs e)
    {
        if(router is StoryDialogueScheduler controller)controller.SkipScene("User explicitly skipped the scene");SetStatus("Current scene speech cleared. Captured dialogue remains in History.");
    }
    private async void ReplayHistory_Click(object sender,RoutedEventArgs e)
    {
        if(DialogueHistory.SelectedItem is not DialogueEvent item)return;
        await StopAllAsync();var run=operation=new CancellationTokenSource();
        try
        {
            ReadSettings();mixer.ProcessId=0;
            var localized=string.IsNullOrWhiteSpace(item.Hindi)?await Providers.Localizer(settings,translationWorker,nllbWorker).TranslateAsync(item.ToDialogue(),profile,[],run.Token):new LocalizedLine(item.Hindi,"neutral",item.TranslationSource);
            var voiceId=profile.VoiceFor(item.Speaker);if(!CharacterVoices.IsKnownVoice(voiceId))voiceId=item.VoiceId;
            var provider=Providers.Voice(settings,voiceWorker,gpuVoiceWorker,SetStatus);
            var key=TranslationTextCache.Hash(localized.Hindi+"|"+voiceId+"|"+provider.Identity);
            var cached=key==item.AudioCacheKey?new DialogueCache().Get(key):null;
            var clip=cached?.Clip??await provider.SynthesizeAsync(localized,voiceId,run.Token);
            await audio.PlayAsync(clip,()=> { Original.Text=item.English;Hindi.Text=localized.Hindi;SetStatus("Replaying remembered dialogue • "+item.Speaker); },run.Token);
        }
        catch(Exception error) { Fail(error); }
        finally { if(operation==run)operation=null;run.Dispose(); }
    }
}
