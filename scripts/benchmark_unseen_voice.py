"""Real local voice samples for human review. No automatic naturalness or emotion score."""
import base64,csv,json,statistics,subprocess,sys,threading,time,wave
from pathlib import Path
import psutil
ROOT=Path(__file__).resolve().parents[1];sys.path.insert(0,str(ROOT/'workers'))
from local_worker import Models
out=ROOT/'artifacts/repair-acceptance/voices';out.mkdir(parents=True,exist_ok=True)
process=psutil.Process();process.nice(psutil.BELOW_NORMAL_PRIORITY_CLASS)
games=[dict(pid=p.pid,name=p.info['name']) for p in psutil.process_iter(['name']) if (p.info['name'] or '').lower()=='gta5_enhanced.exe']
def gpu():
    try:return subprocess.check_output(['nvidia-smi','--query-gpu=name,memory.used,memory.total,utilization.gpu','--format=csv,noheader'],timeout=15,creationflags=0x08000000,text=True).strip()
    except Exception as e:return type(e).__name__
before=gpu();samples=[];stop=threading.Event()
def monitor():
    process.cpu_percent()
    while not stop.wait(.2):samples.append(dict(rssMiB=process.memory_info().rss/1048576,cpuPercentOneCore=process.cpu_percent()))
thread=threading.Thread(target=monitor,daemon=True);thread.start()
rows=[];model=Models('tts')
lines=json.loads((ROOT/'data/acceptance-dialogue.json').read_text(encoding='utf-8'))
for sample in [lines[i] for i in [1,8,19,32,48]]:
    for voice in ['hf_alpha','hf_beta','hm_omega','hm_psi']:
        start=time.perf_counter();result=model.run(dict(text=sample['referenceHindi'],voice=voice));ms=(time.perf_counter()-start)*1000
        name=sample['id']+'-'+voice+'.wav'
        with wave.open(str(out/name),'wb') as audio:
            audio.setnchannels(1);audio.setsampwidth(2);audio.setframerate(result['rate']);audio.writeframes(base64.b64decode(result['pcm']))
        rows.append(dict(id=sample['id'],text=sample['referenceHindi'],voice=voice,file=name,fullGenerationMs=ms,firstChunkReadyMs=ms,audioSeconds=result['durationSeconds'],
            naturalnessRating=None,pronunciationRating=None,characterConsistencyRating=None,expressivenessRating=None,emotionControlSupported=False))
        print(sample['id'],voice,round(ms),'ms',flush=True)
stop.set();thread.join();timings=sorted(r['fullGenerationMs'] for r in rows)
report=dict(model='Kokoro82M promoted FP32 CPU2 arena',count=len(rows),medianMs=statistics.median(timings),p95Ms=timings[-1],
    peakProcessMiB=max(s['rssMiB'] for s in samples),meanCpuNormalized=statistics.mean(s['cpuPercentOneCore'] for s in samples)/psutil.cpu_count(),peakCpuNormalized=max(s['cpuPercentOneCore'] for s in samples)/psutil.cpu_count(),
    gtaProcessesAtStart=games,gpuBefore=before,gpuAfter=gpu(),note='GTA process presence and system GPU totals do not prove active gameplay or app-attributed VRAM. FPS/acoustic latency not measured. Ratings require a human listener. No game audio stored.',samples=rows)
(out/'benchmark.json').write_text(json.dumps(report,indent=2,ensure_ascii=False),encoding='utf-8')
with (out/'human-review.csv').open('w',encoding='utf-8-sig',newline='') as f:
    writer=csv.DictWriter(f,fieldnames=list(rows[0]));writer.writeheader();writer.writerows(rows)
print(json.dumps({k:v for k,v in report.items() if k!='samples'}),flush=True)
