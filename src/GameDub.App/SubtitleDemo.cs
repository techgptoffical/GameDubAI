using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using GameDub.Core;

namespace GameDub.App;
public sealed class SubtitleDemo : Window
{
    private readonly TextBlock subtitle = new() { FontSize = 29, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(30, 0, 30, 0) };
    private readonly List<DialogueSample> samples = Json.Samples();
    private CancellationTokenSource? scene;
    private readonly Border area;
    public SubtitleDemo()
    {
        Title = "GameDub subtitle demo — synthetic test, not a game";
        Width = 1000; Height = 510; ResizeMode = ResizeMode.NoResize; Background = new SolidColorBrush(Color.FromRgb(18, 24, 31));
        var grid = new Grid(); Content = grid;
        var header = new StackPanel { Margin = new Thickness(26, 20, 26, 0), VerticalAlignment = VerticalAlignment.Top };
        header.Children.Add(new TextBlock { Text = "SUBTITLE DEMO", Foreground = Brushes.LightSteelBlue, FontSize = 12 });
        header.Children.Add(new TextBlock { Text = "A repeatable scene for the real capture + OCR pipeline", Foreground = Brushes.White, FontSize = 21, Margin = new Thickness(0, 9, 0, 12) });
        var buttons = new WrapPanel(); header.Children.Add(buttons);
        var play = new Button { Content = "Play scene" }; play.Click += async (_, _) => await PlayAsync();
        var skip = new Button { Content = "Skip / clear subtitle" }; skip.Click += (_, _) => { scene?.Cancel(); subtitle.Text = ""; };
        var unseen = new Button { Content = "New unscripted line" }; unseen.Click += (_, _) => { scene?.Cancel(); subtitle.Text = "The bridge is broken. Find another way."; };
        buttons.Children.Add(play); buttons.Children.Add(skip); buttons.Children.Add(unseen);
        header.Children.Add(new TextBlock { Text = "Enable dubbing in the main window first. Keep this window in front. No video or screen recording is created.", Foreground = Brushes.LightSlateGray, Margin = new Thickness(0, 16, 0, 0) });
        grid.Children.Add(header);
        area = new Border { Background = Brushes.Black, Height = 145, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(25, 250, 25, 0), Child = subtitle };
        grid.Children.Add(area);
        Closed += (_, _) => { scene?.Cancel(); scene?.Dispose(); };
    }
    public GameWindow AsGameWindow() => new(Environment.ProcessId, new WindowInteropHelper(this).Handle, "GameDub-demo", Title);
    public CaptureRegion SubtitleRegion()
    {
        UpdateLayout();
        var bounds = GameWindows.Bounds(new WindowInteropHelper(this).Handle);
        var point = area.PointToScreen(new Point()); var dpi = VisualTreeHelper.GetDpi(this);
        return new((int)point.X - bounds.X, (int)point.Y - bounds.Y, (int)(area.ActualWidth * dpi.DpiScaleX), (int)(area.ActualHeight * dpi.DpiScaleY));
    }
    private async Task PlayAsync()
    {
        scene?.Cancel(); var run = scene = new CancellationTokenSource();
        try
        {
            foreach (var id in new[] { "02", "07", "10", "15" })
            {
                var sample = samples.First(s => s.Id == id); subtitle.Text = $"{sample.Speaker}: {sample.English}";
                await Task.Delay(11000, run.Token); subtitle.Text = ""; await Task.Delay(1300, run.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally { if (scene == run) scene = null; run.Dispose(); }
    }
}
