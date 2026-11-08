using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using GameDub.Core;

namespace GameDub.App;

public partial class MainWindow
{
    private bool refreshingGames;
    private GameWindow? selectedGame;
    private int selectionVersion;
    private readonly DispatcherTimer targetWatch = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly CancellationTokenSource targetLifetime = new();

    private void StartTargetWatch()
    {
        targetWatch.Tick += async (_, _) =>
        {
            if (closing || refreshingGames || Games.IsDropDownOpen) return;
            if (selectedGame is null || !GameWindows.IsAlive(selectedGame)) await RefreshGamesAsync();
        };
        targetWatch.Start();
    }

    private List<GameWindow> AvailableTargets()
    {
        var windows = GameWindows.List().Where(w => !w.Name.Equals("GameDub.App", StringComparison.OrdinalIgnoreCase)).ToList();
        if (demo is { IsLoaded: true })
        {
            var fixture = demo.AsGameWindow();
            if (GameWindows.IsAlive(fixture)) windows.Add(fixture);
        }
        return windows;
    }

    private async Task RefreshGamesAsync()
    {
        if (closing || refreshingGames) return;
        refreshingGames = true;
        try
        {
            var windows = AvailableTargets();
            var next = GameWindows.ChooseSelection(windows, selectedGame);
            Games.ItemsSource = windows;
            Games.SelectedItem = next;
            await ApplyGameSelectionAsync(next);
        }
        catch (Exception error) { Fail(error); }
        finally { refreshingGames = false; }
    }

    private async Task BindGameChoiceAsync(GameWindow game)
    {
        refreshingGames = true;
        try
        {
            var windows = AvailableTargets();
            if (!windows.Any(w => w.Handle == game.Handle && w.ProcessId == game.ProcessId)) windows.Insert(0, game);
            Games.ItemsSource = windows;
            Games.SelectedItem = windows.First(w => w.Handle == game.Handle && w.ProcessId == game.ProcessId);
            await ApplyGameSelectionAsync(game);
        }
        finally { refreshingGames = false; }
    }

    private async Task ApplyGameSelectionAsync(GameWindow? next)
    {
        var previous = selectedGame;
        if (previous?.Handle == next?.Handle && previous?.ProcessId == next?.ProcessId)
        { selectedGame = next; ShowTarget(); return; }
        if (previous is not null && next is not null && previous.ProcessId == next.ProcessId && previous.Name == next.Name && !GameWindows.IsAlive(previous))
        {
            // Capture monitors resolve a replacement HWND only within this same process. Keep
            // their session alive; changing to a different still-live window still stops it.
            selectedGame = next; ShowTarget();
            SetStatus($"Selected game window was recreated; reattached to {next.Title}.");
            return;
        }
        var version = ++selectionVersion;
        selectedGame = next;
        await StopAllAsync();
        if (version != selectionVersion || closing) return;
        if (next is null)
        {
            eventStore?.Dispose();eventStore=null;DialogueHistory.ItemsSource=null;
            profile = new() { DefaultVoice = "hm_omega" };
            RefreshCharacters();
            ProfileJson.Text = JsonSerializer.Serialize(profile, Json.Options); ShowRegion(); ShowTarget();
            SetStatus("Choose your running game above. A closed demo/launcher cannot be used for dubbing.");
            return;
        }
        if (previous?.ProcessId != next.ProcessId || previous.Name != next.Name)
        {
            var id = string.Concat(next.Name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
            var path = Path.Combine(Paths.Profiles, id + ".json");
            try { profile = File.Exists(path) ? Json.Read<GameProfile>(path) : new() { GameId = id, DefaultVoice = "hm_omega" }; }
            catch (Exception error) { profile = new() { GameId = id, DefaultVoice = "hm_omega" }; Fail(error); }
        }
        if(CharacterVoices.MigrateKnownGame(profile))SaveVoiceProfile();
        if(SubtitleImages.RepairClippedGameRegion(profile))SaveVoiceProfile();
        OpenDialogueHistory();
        ProfileJson.Text = JsonSerializer.Serialize(profile, Json.Options);
        ShowRegion(); ShowTarget();
        RefreshCharacters();
        if (session is null)
            SetStatus(next.ProcessId == Environment.ProcessId ? "Test demo selected. Select the real game above to dub actual gameplay." : $"Selected {next.Title} ({next.Name}). Enable English subtitles and choose their region.");
    }

    private void ShowTarget()
    {
        TargetStatus.Text = selectedGame is null ? "No game selected. Pick the actual game window; use the 5-second picker if it is missing."
            : selectedGame.ProcessId == Environment.ProcessId ? "TEST DEMO SELECTED — your actual game is not being dubbed. Choose it in the list above."
            : $"Selected game/window: {selectedGame.Title} • {selectedGame.Name} • PID {selectedGame.ProcessId}";
    }

    private async void PickGame_Click(object sender, RoutedEventArgs e)
    {
        if (closing) return;
        await StopAllAsync();
        PickGameButton.IsEnabled = false;
        try
        {
            SetStatus("Switch to the actual game now. Its foreground window will be selected in 5 seconds; capture will not start automatically.");
            await Task.Delay(5000, targetLifetime.Token);
            var game = GameWindows.FromHandle(GameWindows.GetForegroundWindow());
            if (game is null || game.ProcessId == Environment.ProcessId || game.Name.Equals("GameDub.App", StringComparison.OrdinalIgnoreCase))
            { SetStatus("No game chosen. Click the picker, then switch to the game during the 5-second countdown."); return; }
            await BindGameChoiceAsync(game);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Fail(error); }
        finally { if (!closing) PickGameButton.IsEnabled = true; }
    }
}
