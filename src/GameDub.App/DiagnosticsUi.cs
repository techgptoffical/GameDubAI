using System.IO;
using System.Text.Json;
using System.Windows;
using GameDub.Core;

namespace GameDub.App;

public partial class MainWindow
{
    private PipelineTrace? diagnosticTrace;
    private string? LatestDiagnosticDirectory() => Directory.Exists(Path.Combine(Paths.Root,"diagnostic-sessions"))
        ? Directory.GetDirectories(Path.Combine(Paths.Root,"diagnostic-sessions")).OrderDescending().FirstOrDefault() : null;
    private async void ReplayDiagnostics_Click(object sender,RoutedEventArgs e)
    {
        await StopAllAsync();
        var directory=LatestDiagnosticDirectory();
        if(directory is null) { SetStatus("No diagnostic session. Enable local crop diagnostics, start dubbing, then Stop and replay."); return; }
        var run=operation=new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try
        {
            mixer.ProcessId=0;
            var pipeline=MakePipeline(profile,12000);
            await using var replayRouter=new DialogueRouter(pipeline,mixer,true,12000);
            replayRouter.Error+=SetStatus; replayRouter.Status+=SetStatus;
            SetStatus("Replaying captured OCR timing locally • no game capture or new screenshots");
            await DiagnosticReplay.ReplayOcrAsync(directory,replayRouter,run.Token);
            await replayRouter.WaitForIdleAsync(run.Token);
            SetStatus($"Replay finished • {pipeline.Metrics.Snapshot().Length} playback starts • {replayRouter.DroppedCount} dropped. {directory}");
            UpdateMetrics();
        }
        catch(Exception error) { Fail(error); }
        finally { if(operation==run) operation=null; run.Dispose(); }
    }
    private async void RecheckCrops_Click(object sender,RoutedEventArgs e)
    {
        await StopAllAsync();
        var directory=LatestDiagnosticDirectory(); if(directory is null) { SetStatus("No diagnostic crops available."); return; }
        var run=operation=new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            var records=await Task.Run(async () =>
            {
                var ocr=new WindowsSubtitleOcr(profile.OcrStyle,profile.OcrScale);
                var rows=new List<object>();
                foreach(var frame in DiagnosticReplay.Frames(directory))
                {
                    using var image=new System.Drawing.Bitmap(DiagnosticReplay.CropPath(directory,frame.File));
                    var watch=System.Diagnostics.Stopwatch.StartNew(); var actual=await ocr.RecognizeAsync(image,run.Token);
                    rows.Add(new { frame.File, frame.AtMs, capturedOcr=frame.Ocr, replayOcr=actual, ms=watch.Elapsed.TotalMilliseconds, variant=ocr.LastVariant, confidence=(double?)null });
                }
                return rows;
            },run.Token);
            Json.Write(Path.Combine(directory,"reocr.json"),records);
            SetStatus($"Re-OCRed {records.Count} saved subtitle crops. Results: {directory}\\reocr.json");
        }
        catch(Exception error) { Fail(error); }
        finally { if(operation==run) operation=null; run.Dispose(); }
    }
}
