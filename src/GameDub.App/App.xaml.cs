using System.Windows;
using System.IO;
using GameDub.Core;
namespace GameDub.App;
public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            try { Directory.CreateDirectory(Paths.Root); File.WriteAllText(Path.Combine(Paths.Root,"last-error.txt"), e.Exception.GetType().Name + ": " + e.Exception.Message + "\n" + e.Exception.StackTrace); } catch (IOException) { }
            e.Handled = true; Shutdown(1);
        };
    }
}
