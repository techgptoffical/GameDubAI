using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GameDub.App;

/// <summary>Asks DWM to draw the window caption in dark mode. Purely cosmetic; failures are ignored.</summary>
internal static class DarkTitleBar
{
    private const int DwmUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    private const uint FrameChanged = 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020; // NOSIZE|NOMOVE|NOZORDER|NOACTIVATE|FRAMECHANGED

    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var enabled = 1;
        try { DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int)); SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, FrameChanged); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
}
