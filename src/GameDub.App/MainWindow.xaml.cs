using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GameDub.Core;

namespace GameDub.App;

public record VoiceChoice(string Id, string Label);
public partial class MainWindow : Window
{
    private readonly AppSettings settings = AppSettings.Load();
    private readonly LocalWorker translationWorker = new("translate");
    private readonly LocalWorker voiceWorker = new("tts");
    private readonly LocalWorker asrWorker = new("asr");
    private readonly WasapiPlayer audio = new();
    private readonly GameDuckingPlayer mixer;
    private GameProfile profile = new() { DefaultVoice = "hf_alpha" };
    private CancellationTokenSource? operation;
    private CancellationTokenSource? session;
    private DialogueRouter? router;
    private DubbingPipeline? currentPipeline;
    private AudioClip? lastClip;
    private string? lastVoiceId;
    private bool initialized, closing;
    private SubtitleDemo? demo;
    private readonly List<LatencyRecord> measurements = new();
    private readonly Stopwatch uptime = Stopwatch.StartNew();
    public MainWindow()
    {
        InitializeComponent();
        mixer = new(audio);
        Samples.ItemsSource = Json.Samples(); Samples.SelectedIndex = 1;
        string[] emotions = ["neutral", "angry", "frightened", "sad", "excited", "sarcastic", "whispering", "shouting", "urgent", "calm", "threatening", "gentle", "restrained"];
        Emotion.ItemsSource = emotions; Emotion.SelectedIndex = 0;
        VoiceChoice[] voices = [new("hf_alpha", "Alpha · Hindi female"), new("hf_beta", "Beta · Hindi female"), new("hm_omega", "Omega · Hindi male"), new("hm_psi", "Psi · Hindi male")];
        VoiceA.ItemsSource = voices; VoiceB.ItemsSource = voices; VoiceA.SelectedIndex = 0; VoiceB.SelectedIndex = 2;
        Rating.ItemsSource = new[] { "1 · poor", "2 · weak", "3 · usable", "4 · good", "5 · excellent" }; Rating.SelectedIndex = 2;
        SelectCombo(Mode, settings.PerformanceMode); SelectCombo(Translator, settings.Translator); SelectCombo(Tts, settings.Tts);
        SelectCombo(InputMode, settings.InputMode);
        Volume.Value = settings.Volume; Ducking.IsChecked = settings.DuckGameAudio;
        RefreshOutputs();
        ProfileJson.Text = JsonSerializer.Serialize(profile, Json.Options);
        initialized = true;
        Loaded += async (_, _) =>
        {
            await RefreshGamesAsync();
            if (Environment.GetCommandLineArgs().Contains("--target-smoke-test"))
            {
                Json.Write(Path.Combine(Paths.Workspace, "artifacts", "target-selection-smoke.json"), new
                { selectedProcess = selectedGame?.Name, selectedTitle = selectedGame?.Title, selectedPid = selectedGame?.ProcessId,
                  alive = selectedGame is not null && GameWindows.IsAlive(selectedGame), profileGameId = profile.GameId,
                  hasSubtitleRegion = profile.Region is not null, targetHint = TargetStatus.Text });
                Close(); return;
            }
            StartTargetWatch(); await WarmModelsAsync();
        };
        Activated += async (_, _) => { if (initialized && !closing) await RefreshGamesAsync(); };
    }
    private static void SelectCombo(ComboBox combo, string value)
    {
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Content == value) ?? combo.Items[0];
    }
    private static string Selected(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
    private async Task WarmModelsAsync()
    {
        StartButton.IsEnabled = false;
        SetStatus("Loading offline translation and Hindi voice models…");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await Task.WhenAll(translationWorker.WarmAsync(timeout.Token), voiceWorker.WarmAsync(timeout.Token));
            Readiness.Text = "Local models ready • Windows subtitle OCR • selected-device WASAPI output";
            SetStatus("Ready. Try Voice lab, or open the subtitle demo."); StartButton.IsEnabled = true;
            Json.Write(Path.Combine(Paths.Root, "startup-health.json"), new { at = DateTimeOffset.Now, ready = true, runtime = File.Exists(Path.Combine(Paths.Workspace, "runtime", "python.exe")) ? "bundled Python" : "project venv", translationMiB = translationWorker.WorkingSetBytes / 1048576.0, voiceMiB = voiceWorker.WorkingSetBytes / 1048576.0 });
        }
        catch (Exception e)
        {
            Readiness.Text = "Local model setup needs attention"; Fail(e); StartButton.IsEnabled = true;
            Json.Write(Path.Combine(Paths.Root, "startup-health.json"), new { at = DateTimeOffset.Now, ready = false, error = e.Message });
        }
        if (Environment.GetCommandLineArgs().Contains("--smoke-test")) Close();
    }
    private void SetStatus(string message)
    {
        if (closing) return;
        Dispatcher.Invoke(() =>
        {
            Status.Text = message;
            Log.AppendText($"{DateTime.Now:HH:mm:ss} {message}\n");
            if (Log.Text.Length > 14000) Log.Text = Log.Text[^10000..];
            Log.ScrollToEnd();
            using var p = Process.GetCurrentProcess();
            ResourceInfo.Text = $"App working set {p.WorkingSet64 / 1048576.0:0} MiB • uptime {uptime.Elapsed:mm\\:ss}\nPython model workers are separate; export includes their working sets. GPU/FPS impact not measured.";
        });
    }
    private void Fail(Exception error)
    {
        if (error is OperationCanceledException) { SetStatus("Stopped. Obsolete dialogue cancelled."); return; }
        SetStatus(error is System.Net.Http.HttpRequestException ? "Unexpected network operation blocked in offline build." : error.Message);
    }
    private void RefreshOutputs()
    {
        try { Outputs.ItemsSource = WasapiPlayer.Devices(); Outputs.SelectedItem = Outputs.Items.OfType<AudioDevice>().FirstOrDefault(d => d.Id == settings.OutputDevice) ?? Outputs.Items[0]; }
        catch (Exception e) { Fail(e); }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshGamesAsync();
    private void RefreshOutputs_Click(object sender, RoutedEventArgs e) => RefreshOutputs();
    private async void Games_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || refreshingGames) return;
        await ApplyGameSelectionAsync(Games.SelectedItem as GameWindow);
    }
    private void ShowRegion() => RegionStatus.Text = profile.Region is { } r
        ? $"Subtitle region: x={r.X}, y={r.Y}, {r.Width} × {r.Height} client pixels"
        : "Select a small subtitle region. Borderless/windowed mode is recommended.";
    private async void Region_Click(object sender, RoutedEventArgs e)
    {
        if (Games.SelectedItem is not GameWindow game) { SetStatus("Select a running game or demo first."); return; }
        if (!GameWindows.IsAlive(game)) { await RefreshGamesAsync(); SetStatus("That selected window has closed. Select the running game, then choose its subtitle region."); return; }
        await StopAllAsync();
        try
        {
            GameWindows.SetForegroundWindow(game.Handle);
            var selector = new RegionSelector(game);
            if (selector.ShowDialog() == true)
            {
                profile.Region = selector.Region; ShowRegion();
                ProfileJson.Text = JsonSerializer.Serialize(profile, Json.Options);
                Json.Write(Path.Combine(Paths.Profiles, profile.GameId + ".json"), profile);
            }
        }
        catch (Exception error) { Fail(error); }
    }
    private async void Demo_Click(object sender, RoutedEventArgs e)
    {
        await StopAllAsync();
        if (demo is null || !demo.IsLoaded)
        {
            demo = new SubtitleDemo(); demo.Show();
            // A separate window in the same process is deliberately selectable for our integration fixture.
            var openedDemo = demo;
            var game = openedDemo.AsGameWindow();
            openedDemo.Closed += async (_, _) =>
            {
                if (ReferenceEquals(demo, openedDemo)) demo = null;
                if (closing || selectedGame?.Handle != game.Handle) return;
                await RefreshGamesAsync();
                SetStatus(selectedGame is null ? "Test demo closed. Choose your running game above." : $"Test demo closed. Selected {selectedGame.Title}; choose its subtitle region before starting.");
            };
            await BindGameChoiceAsync(game);
            profile = new() { GameId = "subtitle-demo", DefaultVoice = "hf_alpha", Region = demo.SubtitleRegion(), Voices = new() { ["Ravi"] = "hm_omega", ["Meera"] = "hf_alpha", ["Vikram"] = "hm_psi" } };
            ProfileJson.Text = JsonSerializer.Serialize(profile, Json.Options); ShowRegion();
            SetStatus("Demo ready. Press Start dubbing, then Play scene in the demo window.");
        }
        else demo.Activate();
    }
    private void ReadSettings()
    {
        settings.Translator = Selected(Translator); settings.Tts = Selected(Tts); settings.PerformanceMode = Selected(Mode);
        settings.InputMode = Selected(InputMode);
        settings.OutputDevice = (Outputs.SelectedItem as AudioDevice)?.Id ?? ""; settings.Volume = (float)Volume.Value;
        settings.DuckGameAudio = Ducking.IsChecked == true;
        audio.DeviceId = settings.OutputDevice; audio.Volume = settings.Volume;
        mixer.Enabled = settings.DuckGameAudio; mixer.Level = settings.DuckLevel;
        EngineLabel.Text = settings.Tts.StartsWith("Kokoro") ? "Kokoro · local neural Hindi" : "eSpeak NG · robotic test fallback";
    }
    private DubbingPipeline MakePipeline(GameProfile selectedProfile, int? budget = null)
    {
        ReadSettings();
        var ageBudget = settings.PerformanceMode switch { "Quality" => Math.Max(12000, settings.MaxAgeMs), "Performance" => Math.Min(4000, settings.MaxAgeMs), _ => Math.Max(8000, settings.MaxAgeMs) };
        var pipeline = new DubbingPipeline(Providers.Localizer(settings, translationWorker), Providers.Voice(settings, voiceWorker), mixer, new DialogueCache(), selectedProfile, budget ?? Math.Clamp(ageBudget, 500, 30000));
        pipeline.Localized += (line, localized) => Dispatcher.Invoke(() => { Original.Text = line.English; Hindi.Text = LabHindi.Text = localized.Hindi; ReplayButton.IsEnabled = false; });
        pipeline.Playing += (line, localized) => Dispatcher.Invoke(() => { Original.Text = line.English; Hindi.Text = localized.Hindi; LineSource.Text = $"Speaking • {line.Speaker} • {localized.Source}"; });
        pipeline.Ready += (line, localized, clip) => Dispatcher.Invoke(() =>
        {
            Original.Text = line.English; Hindi.Text = LabHindi.Text = localized.Hindi;
            LineSource.Text = $"{line.Speaker} • {localized.Source} • delivery hint: {localized.Emotion}";
            lastClip = clip; ReplayButton.IsEnabled = true;
            lastVoiceId = selectedProfile.VoiceFor(line.Speaker);
        });
        pipeline.Status += SetStatus;
        currentPipeline = pipeline;
        return pipeline;
    }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (Games.SelectedItem is not GameWindow game) { SetStatus("Select a running game window first."); return; }
        if (!GameWindows.IsAlive(game)) { await RefreshGamesAsync(); SetStatus("The previously selected window closed. Choose the running game and its subtitle region, then start again."); return; }
        var useAudio = Selected(InputMode).StartsWith("Game audio");
        var automatic = Selected(InputMode).StartsWith("Automatic");
        if (!useAudio && profile.Region is null) { SetStatus("Select a subtitle region first."); return; }
        if (useAudio && game.ProcessId == Environment.ProcessId) { SetStatus("Audio mode needs an external game process; the subtitle demo belongs to GameDub and is excluded to prevent feedback."); return; }
        await StopAllAsync();
        StartButton.IsEnabled = false;
        mixer.ProcessId = game.ProcessId == Environment.ProcessId ? 0 : game.ProcessId;
        var running = session = new CancellationTokenSource();
        try
        {
            var pipeline = MakePipeline(profile);
            var activeRouter = router = new DialogueRouter(pipeline, mixer, preserveLines: settings.PerformanceMode != "Performance", maxDelayMs: settings.PerformanceMode == "Quality" ? 12000 : 8000);
            activeRouter.Error += SetStatus;
            activeRouter.Status += SetStatus;
            if (automatic && game.ProcessId != Environment.ProcessId)
            {
                var monitor = new HybridMonitor(asrWorker); monitor.Status += SetStatus;
                await Task.Run(() => monitor.RunAsync(game, profile.Region!, activeRouter, running.Token));
            }
            else if (useAudio)
            {
                var monitor = new LiveAudioMonitor(asrWorker); monitor.Status += SetStatus;
                await Task.Run(() => monitor.RunAsync(game, activeRouter, running.Token));
            }
            else
            {
                var monitor = new SubtitleMonitor(); monitor.Status += SetStatus;
                SetStatus(game.ProcessId == Environment.ProcessId ? "TEST DEMO dubbing enabled. This targets the demo, not your game." : $"Dubbing enabled for {game.Title} ({game.Name}). Return to that game with English subtitles visible.");
                await Task.Run(() => monitor.RunAsync(game, profile.Region!, activeRouter, running.Token));
            }
        }
        catch (Exception error) { Fail(error); }
        finally
        {
            if (session == running)
            {
                session = null;
                if (router is { } r) { router = null; await r.DisposeAsync(); }
                StartButton.IsEnabled = true; UpdateMetrics();
            }
            running.Dispose();
            if (!closing && selectedGame is { } selected && !GameWindows.IsAlive(selected)) await RefreshGamesAsync();
        }
    }
    private async Task StopAllAsync()
    {
        operation?.Cancel();
        var running = session; session = null; running?.Cancel();
        if (router is { } old) { router = null; await old.DisposeAsync(); }
        audio.Stop(); StartButton.IsEnabled = true; UpdateMetrics();
        // Session owner disposes its CTS only after capture exits; cancellation can still be observed.
    }
    private async void Stop_Click(object sender, RoutedEventArgs e) { await StopAllAsync(); SetStatus("Stopped. Translation, generation and playback cancelled."); }
    private void Samples_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Samples.SelectedItem is not DialogueSample sample || EnglishInput is null) return;
        EnglishInput.Text = sample.English; SpeakerInput.Text = sample.Speaker; ContextInput.Text = sample.Context;
        Emotion.SelectedItem = sample.Emotion;
    }
    private async Task RunLabAsync(bool compare)
    {
        await StopAllAsync();
        ReadSettings();
        var run = operation = new CancellationTokenSource();
        try
        {
            mixer.ProcessId = 0;
            var line = new Dialogue(EnglishInput.Text.Trim(), SpeakerInput.Text.Trim(), Emotion.SelectedItem?.ToString() ?? "neutral", ContextInput.Text.Trim());
            if (line.English.Length is < 1 or > 1500) throw new InvalidOperationException("Enter 1–1500 characters of English dialogue.");
            var choices = compare ? new[] { (VoiceChoice)VoiceA.SelectedItem, (VoiceChoice)VoiceB.SelectedItem } : new[] { (VoiceChoice)VoiceA.SelectedItem };
            foreach (var choice in choices)
            {
                run.Token.ThrowIfCancellationRequested();
                var labProfile = new GameProfile { GameId = "voice-lab", DefaultVoice = settings.Tts.StartsWith("eSpeak") ? (choice.Id.StartsWith("hf") ? "hi+f3" : "hi") : choice.Id, Setting = profile.Setting, Glossary = new(profile.Glossary), Relationships = new(profile.Relationships), ReviewedLines = new(profile.ReviewedLines) };
                var pipeline = MakePipeline(labProfile, 60000);
                SetStatus($"Voice {choice.Label}…");
                var record = await pipeline.ProcessAsync(line, run.Token);
                measurements.Add(record); UpdateMetrics();
                await Task.Delay(200, run.Token);
            }
        }
        catch (Exception error) { Fail(error); }
        finally { if (operation == run) operation = null; run.Dispose(); }
    }
    private async void Generate_Click(object sender, RoutedEventArgs e) => await RunLabAsync(false);
    private async void Compare_Click(object sender, RoutedEventArgs e) => await RunLabAsync(true);
    private async void Replay_Click(object sender, RoutedEventArgs e)
    {
        if (lastClip is null) return;
        await StopAllAsync(); var run = operation = new CancellationTokenSource();
        try { ReadSettings(); await audio.PlayAsync(lastClip, () => SetStatus("Replaying last Hindi clip…"), run.Token); }
        catch (Exception error) { Fail(error); }
        finally { if (operation == run) operation = null; run.Dispose(); }
    }
    private void UpdateMetrics() { if (currentPipeline is not null) Metrics.Text = currentPipeline.Metrics.Summary(); }
    private void Tabs_Changed(object sender, SelectionChangedEventArgs e) { if (initialized) UpdateMetrics(); }
    private void Mode_Changed(object sender, SelectionChangedEventArgs e) { if (initialized) ReadSettings(); }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        await StopAllAsync();
        try
        {
            var edited = JsonSerializer.Deserialize<GameProfile>(ProfileJson.Text, Json.Options) ?? throw new InvalidDataException("Invalid profile JSON.");
            // Profile identifiers may never escape the project's profile directory.
            if (string.IsNullOrWhiteSpace(edited.GameId) || edited.GameId.Any(c => !char.IsLetterOrDigit(c) && c is not '-' and not '_')) throw new InvalidDataException("GameId must contain letters, numbers, '-' or '_'.");
            if (edited.Glossary is null || edited.Voices is null || edited.Relationships is null || edited.ReviewedLines is null) throw new InvalidDataException("Profile maps cannot be null.");
            profile = edited; ReadSettings(); settings.Save(); Json.Write(Path.Combine(Paths.Profiles, profile.GameId + ".json"), profile); ShowRegion();
            SetStatus("Local settings and game voice profile saved.");
        }
        catch (Exception error) { Fail(error); }
    }
    private void Rating_Click(object sender, RoutedEventArgs e)
    {
        if (lastClip is null || !ReplayButton.IsEnabled) { SetStatus("Wait for a complete Hindi playback before saving a rating."); return; }
        Directory.CreateDirectory(Paths.Root);
        File.AppendAllText(Path.Combine(Paths.Root, "human-ratings.jsonl"), JsonSerializer.Serialize(new { at = DateTimeOffset.Now, english = Original.Text, hindi = LabHindi.Text, voice = lastVoiceId, rating = Rating.SelectedIndex + 1, engine = lastClip.Identity }) + "\n");
        SetStatus("Human rating saved locally. No automatic professional-quality score.");
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var workers = new[] { translationWorker, voiceWorker, asrWorker }.Select(w => new { pid = w.ProcessId, workingSetMiB = w.WorkingSetBytes / 1048576.0 }).ToArray();
            Json.Write(Path.Combine(Paths.Root, "diagnostics.json"), new { at = DateTimeOffset.Now, voiceLab = measurements, currentSession = currentPipeline?.Metrics.Snapshot(), modelWorkers = workers, note = "Only GameDub-owned workers. No command lines, FPS or acoustic measurement." });
            SetStatus("Exported local-settings/diagnostics.json");
        }
        catch (Exception error) { Fail(error); }
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        targetWatch.Stop();
        targetLifetime.Cancel();
        e.Cancel = true;
        await StopAllAsync();
        closing = true; demo?.Close(); translationWorker.Dispose(); voiceWorker.Dispose(); asrWorker.Dispose(); audio.Dispose();
        // Closing can complete synchronously when idle. Queue the second Close until WPF has
        // returned from the cancelled Closing event, avoiding a reentrant-close exception.
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}
