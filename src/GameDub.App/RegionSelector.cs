using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using GameDub.Core;

namespace GameDub.App;
public sealed class RegionSelector : Window
{
    private readonly Canvas canvas = new();
    private readonly Rectangle selection = new() { Stroke = Brushes.DodgerBlue, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(55, 40, 120, 220)) };
    private Point start;
    private readonly System.Drawing.Rectangle bounds;
    public CaptureRegion? Region { get; private set; }
    public RegionSelector(GameWindow game)
    {
        bounds = GameWindows.Bounds(game.Handle);
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(75, 0, 0, 0)); Topmost = true; ShowInTaskbar = false;
        Content = canvas; Cursor = Cursors.Cross;
        canvas.Children.Add(new TextBlock { Text = "Drag around English subtitles · Esc to cancel", Foreground = Brushes.White, FontSize = 20, Margin = new Thickness(25), Background = Brushes.Black, Padding = new Thickness(12) });
        canvas.Children.Add(selection);
        Loaded += (_, _) => GameWindows.SetWindowPos(new WindowInteropHelper(this).Handle, -1, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0040);
        MouseLeftButtonDown += (_, e) => { start = e.GetPosition(canvas); CaptureMouse(); Canvas.SetLeft(selection, start.X); Canvas.SetTop(selection, start.Y); selection.Width = selection.Height = 0; };
        MouseMove += (_, e) =>
        {
            if (!IsMouseCaptured) return;
            var p = e.GetPosition(canvas);
            Canvas.SetLeft(selection, Math.Min(start.X, p.X)); Canvas.SetTop(selection, Math.Min(start.Y, p.Y));
            selection.Width = Math.Abs(start.X - p.X); selection.Height = Math.Abs(start.Y - p.Y);
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!IsMouseCaptured) return;
            ReleaseMouseCapture();
            var first = PointToScreen(start); var last = PointToScreen(e.GetPosition(canvas));
            var x = Math.Clamp((int)Math.Min(first.X, last.X) - bounds.X, 0, bounds.Width);
            var y = Math.Clamp((int)Math.Min(first.Y, last.Y) - bounds.Y, 0, bounds.Height);
            var w = Math.Min((int)Math.Abs(first.X - last.X), bounds.Width - x);
            var h = Math.Min((int)Math.Abs(first.Y - last.Y), bounds.Height - y);
            if (w >= 30 && h >= 20) { Region = new(x, y, w, h); DialogResult = true; }
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; };
    }
}
