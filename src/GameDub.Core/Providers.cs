using System.Diagnostics;
using System.Text;

namespace GameDub.Core;

public sealed class EspeakVoice : IVoiceProvider
{
    public static string? Executable => new[]
    {
        Environment.GetEnvironmentVariable("GAMEDUB_ESPEAK_PATH"),
        @"C:\Program Files\eSpeak NG\espeak-ng.exe", @"C:\Program Files (x86)\eSpeak NG\espeak-ng.exe"
    }.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
    public string Identity => "espeak-ng-offline-test-v1:rate155";
    public async Task<AudioClip> SynthesizeAsync(LocalizedLine line, string voice, CancellationToken token)
    {
        var exe = Executable ?? throw new InvalidOperationException("eSpeak NG test fallback not found. Select the bundled Kokoro Hindi voice in Settings.");
        var info = new ProcessStartInfo(exe) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false) };
        // Two lightweight configurations, not two distinct cinematic performers.
        foreach (var arg in new[] { "--stdin", "--stdout", "-v", voice == "hi+f3" ? "hi+f3" : "hi", "-s", "155" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Could not start eSpeak NG.");
        using var registration = token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        using var audio = new MemoryStream();
        var output = process.StandardOutput.BaseStream.CopyToAsync(audio, token);
        var errors = process.StandardError.ReadToEndAsync(token);
        await process.StandardInput.WriteLineAsync(line.Hindi.AsMemory(), token);
        process.StandardInput.Close();
        await Task.WhenAll(output, process.WaitForExitAsync(token));
        await errors;
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0 || audio.Length < 44) throw new IOException("eSpeak NG failed to produce a Hindi WAV file.");
        return new(audio.ToArray(), "wav", Identity);
    }
}

