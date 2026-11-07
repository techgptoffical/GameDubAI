"""Actual offline model evaluation. No automatic naturalness/acting score."""
import json,time,sys,wave,base64,threading,statistics
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'workers'))
from local_worker import Models
import psutil

OUT=ROOT/'artifacts'/'model-evaluation';OUT.mkdir(parents=True,exist_ok=True)
samples=json.loads((ROOT/'data'/'dialogue-tests.json').read_text(encoding='utf-8'))
process=psutil.Process(); snapshots=[];stop=threading.Event()
def monitor():
 process.cpu_percent()
 while not stop.wait(.2):
  snapshots.append({'rssMiB':process.memory_info().rss/1024**2,'cpuPercentOneCore':process.cpu_percent()})
thread=threading.Thread(target=monitor,daemon=True);thread.start()
records=[];mt=Models('translate')
for sample in samples:
 result=mt.run({'text':sample['english']})
 records.append({'id':sample['id'],'english':sample['english'],'reference':sample['hindi'],'actualModelHindi':result['hindi'],'context':sample['context'],'inferenceMs':result['inferenceMs'],'humanRatings':None})
(OUT/'raw-translation-evaluation.json').write_text(json.dumps(records,indent=2,ensure_ascii=False),encoding='utf-8')
print('Actual unmodified MT outputs saved for',len(records),'lines',flush=True)
tts=Models('tts');voices=[]
for sample in samples[:10]:
 for voice in ['hf_alpha','hm_omega']:
  t=time.perf_counter();result=tts.run({'text':sample['hindi'],'voice':voice})
  path=OUT/f"{sample['id']}-{voice}.wav"
  with wave.open(str(path),'wb') as audio:
   audio.setnchannels(1);audio.setsampwidth(2);audio.setframerate(result['rate']);audio.writeframes(base64.b64decode(result.pop('pcm')))
  record={'id':sample['id'],'voice':voice,'emotionScenario':sample['emotion'],'emotionControlSupported':False,'referenceHindi':sample['hindi'],'file':str(path.relative_to(ROOT)),'wallMs':(time.perf_counter()-t)*1000,**result}
  voices.append(record);print('voice sample',sample['id'],voice,round(record['wallMs']),'ms',flush=True)
stop.set();thread.join()
timings=sorted(v['wallMs'] for v in voices)
summary={'count':len(voices),'medianMs':statistics.median(timings),'p95Ms':timings[max(0,__import__('math').ceil(len(timings)*.95)-1)],'worstMs':max(timings),'peakRssMiB':max(s['rssMiB'] for s in snapshots),'peakCpuPercentOneCore':max(s['cpuPercentOneCore'] for s in snapshots),'meanCpuPercentOneCore':statistics.mean(s['cpuPercentOneCore'] for s in snapshots),'logicalCpus':psutil.cpu_count(),'notes':['Synthesis includes full-buffer generation; not first-audible latency.','Two voice configurations, same model family.','No automatic naturalness, Hindi accuracy or acting score.','No game running; no FPS measurement.','CPU percent uses psutil convention: 100% equals one logical CPU.']}
(OUT/'voice-benchmark.json').write_text(json.dumps({'summary':summary,'samples':voices},indent=2,ensure_ascii=False),encoding='utf-8')
print(json.dumps(summary,indent=2),flush=True)
