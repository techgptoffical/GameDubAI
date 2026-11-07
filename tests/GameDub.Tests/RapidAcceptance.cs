using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text.Json;
using GameDub.Core;

internal sealed record AcceptanceLine(string Id,string Category,string English,string ReferenceHindi);
internal static class RapidAcceptance
{
    private sealed record Slot(AcceptanceLine Line,double Begin,double End,int Index);
    internal static async Task RunAsync(string output,CancellationToken token,IDialogueSink? sink=null,PipelineTrace? trace=null)
    {
        var lines=Json.Read<List<AcceptanceLine>>(Path.Combine(Paths.Workspace,"data","acceptance-dialogue.json"));
        var fixtures=Json.Samples().Select(s=>Text.Key(s.English)).ToHashSet();
        if(lines.Count<50 || lines.Any(l=>fixtures.Contains(Text.Key(l.English))))throw new Exception("Acceptance set must contain 50 unseen lines");
        Directory.CreateDirectory(output);
        var styles=new List<object>();
        foreach(var style in sink is null?new[]{"Raw","Auto"}:new[]{"Raw"})
        {
            var slots=new List<Slot>(); double at=0;
            for(var i=0;i<lines.Count;i++) { var duration=sink is null?(i%3==0?500:850):Math.Clamp(lines[i].English.Split(' ').Length/2.8*1000,800,5000); slots.Add(new(lines[i],at,at+duration,i)); at+=duration+100; }
            var ocr=new WindowsSubtitleOcr(style,style=="Auto"?0:1); var detector=new SubtitleChangeDetector();
            using(var warm=Render("Ready.",0))await ocr.RecognizeAsync(warm,token);
            using var process=Process.GetCurrentProcess(); var cpuStart=process.TotalProcessorTime;
            var clock=Stopwatch.StartNew(); var detected=new HashSet<string>(); var records=new List<object>(); var times=new List<double>(); var latencies=new List<double>();
            var epoch=DateTimeOffset.UtcNow;
            var duplicates=0; var falseEvents=0; var frames=0; var peakMiB=0.0; var saved=new HashSet<int>();
            while(clock.Elapsed.TotalMilliseconds<at)
            {
                token.ThrowIfCancellationRequested();
                var frameStart=clock.Elapsed.TotalMilliseconds;
                var slot=slots.FirstOrDefault(s=>frameStart>=s.Begin && frameStart<s.End);
                var text=slot?.Line.English ?? "";
                if(slot is not null && slot.Index%7==0 && frameStart-slot.Begin<120)text=string.Join(' ',text.Split(' ').Take(3));
                using var bitmap=Render(text,slot?.Index??0);
                var timer=Stopwatch.StartNew(); var raw=await ocr.RecognizeAsync(bitmap,token); times.Add(timer.Elapsed.TotalMilliseconds); frames++;
                var observed=detector.Observe(raw,TimeSpan.FromMilliseconds(frameStart));
                trace?.Write("ocr",null,new { text=raw,ocrMs=timer.Elapsed.TotalMilliseconds,confidence=(double?)null,detectorEvent=observed.Event.ToString(),controlled=true });
                trace?.Frame(bitmap,raw,timer.Elapsed.TotalMilliseconds);
                if(observed.Event==SubtitleEvent.Cleared)sink?.SubtitleCleared();
                if(slot is not null && saved.Add(slot.Index))bitmap.Save(Path.Combine(output,$"{style}-{slot.Line.Id}.png"));
                if(observed.Event==SubtitleEvent.Line)
                {
                    var exact=slot is not null && Key(observed.Text)==Key(slot.Line.English);
                    if(exact)
                    {
                        if(!detected.Add(slot!.Line.Id))duplicates++;
                        else latencies.Add(clock.Elapsed.TotalMilliseconds-slot!.Begin);
                    }
                    else falseEvents++;
                    if(sink is not null)
                    {
                        var dialogue=Text.Parse(observed.Text) with { FirstSeenAt=epoch+TimeSpan.FromMilliseconds(slot?.Begin??frameStart) };
                        trace?.Write("detected",dialogue,new { english=dialogue.English,firstObservedAt=dialogue.FirstSeenAt,controlled=true });
                        sink.Submit(dialogue);
                    }
                    records.Add(new { atMs=frameStart,expected=slot?.Line.English,actual=observed.Text,exact,variant=ocr.LastVariant });
                }
                process.Refresh(); peakMiB=Math.Max(peakMiB,process.WorkingSet64/1048576.0);
                var remaining=100-(clock.Elapsed.TotalMilliseconds-frameStart);
                if(remaining>0)await Task.Delay(TimeSpan.FromMilliseconds(remaining),token);
            }
            times.Sort(); latencies.Sort(); process.Refresh();
            double P(List<double> values,double p)=>values.Count==0?0:values[Math.Clamp((int)Math.Ceiling(values.Count*p)-1,0,values.Count-1)];
            var report=new { style,visibleEvents=lines.Count,recognizedEvents=detected.Count,recall=detected.Count/(double)lines.Count,missing=lines.Where(l=>!detected.Contains(l.Id)).Select(l=>l.Id).ToArray(),
                duplicateEvents=duplicates,falseEvents,frames,medianOcrMs=P(times,.5),p95OcrMs=P(times,.95),medianDetectionMs=P(latencies,.5),p95DetectionMs=P(latencies,.95),
                peakProcessMiB=peakMiB,cpuPercent=(process.TotalProcessorTime-cpuStart).TotalMilliseconds/clock.Elapsed.TotalMilliseconds/Environment.ProcessorCount*100,records,
                note=sink is null?"Real wall-clock rendered subtitle replay. 500/850 ms events with 100 ms gaps; some progressive prefixes. Includes OCR/stabilization; no translation or TTS. Confidence unavailable. Not GTA recall or FPS."
                    :"Real wall-clock rendered dialogue at 2.8 English words/second, minimum 800 ms. Native OCR submits into real local MT/TTS and WASAPI independently. Controlled content, not GTA." };
            Json.Write(Path.Combine(output,$"rapid-{style}.json"),report); styles.Add(report);
            Console.WriteLine($"Rapid {style}: {detected.Count}/{lines.Count}, duplicates={duplicates}, falseEvents={falseEvents}, OCR median={P(times,.5):0} ms p95={P(times,.95):0} ms");
        }
        Json.Write(Path.Combine(output,"rapid-summary.json"),styles);
    }
    private static string Key(string value)=>new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static Bitmap Render(string text,int index)
    {
        var bitmap=new Bitmap(1240,132); using var g=Graphics.FromImage(bitmap);
        g.SmoothingMode=SmoothingMode.AntiAlias; g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
        using var background=new LinearGradientBrush(new Point(0,0),new Point(1240,132),Color.FromArgb(15+index%4*20,30,45),Color.FromArgb(100,110,120)); g.FillRectangle(background,0,0,1240,132);
        var size=index%5==0?20:26; using var font=new Font(index%2==0?"Segoe UI":"Arial",size,FontStyle.Bold);
        if(index%4==0 && text.Split(' ').Length>7) { var words=text.Split(' '); text=string.Join(' ',words.Take(6))+"\n"+string.Join(' ',words.Skip(6)); }
        using var format=new StringFormat { Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center };
        foreach(var delta in new[]{(-1,-1),(1,-1),(-1,1),(1,1)})g.DrawString(text,font,Brushes.Black,new RectangleF(delta.Item1,delta.Item2,1240,132),format);
        g.DrawString(text,font,Brushes.White,new RectangleF(0,0,1240,132),format); return bitmap;
    }
}
