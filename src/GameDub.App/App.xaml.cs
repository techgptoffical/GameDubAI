using System.Windows;
using System.IO;
using GameDub.Core;
namespace GameDub.App;
public partial class App : Application
{
    private Mutex? instanceMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        var root=System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Paths.Workspace.ToUpperInvariant()));
        instanceMutex=new Mutex(true,"Local\\GameDubAI-"+Convert.ToHexString(root)[..16],out var created);
        if(!created)
        {
            instanceMutex.Dispose();instanceMutex=null;
            MessageBox.Show("GameDub AI is already running for this project. Use the existing window to avoid loading duplicate models.","GameDub AI");
            Shutdown();return;
        }
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if(instanceMutex is not null) { instanceMutex.ReleaseMutex();instanceMutex.Dispose(); }
        base.OnExit(e);
    }
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            try { Directory.CreateDirectory(Paths.Root); File.WriteAllText(Path.Combine(Paths.Root,"last-error.txt"), e.Exception.GetType().Name + ": " + e.Exception.Message + "\n" + e.Exception.StackTrace); } catch (IOException) { }
            e.Handled = true; Shutdown(1);
        };
    }
}
