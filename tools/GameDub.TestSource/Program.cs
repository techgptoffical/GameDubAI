using GameDub.Core;
using System.Diagnostics;

if (args.Length > 0)
{
    using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(40));
    while (!File.Exists(args[0])) await Task.Delay(50, wait.Token);
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
