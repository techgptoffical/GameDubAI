"""Unseen inputs go directly to real models. Reference wording is evaluation-only."""
import csv,json,statistics,sys,time
from pathlib import Path
import psutil
ROOT=Path(__file__).resolve().parents[1];sys.path.insert(0,str(ROOT/'workers'))
from marian import Marian
from marian_ct2 import MarianCT2
from nllb_ct2 import NllbCT2
psutil.Process().nice(psutil.BELOW_NORMAL_PRIORITY_CLASS)
samples=json.loads((ROOT/'data/acceptance-dialogue.json').read_text(encoding='utf-8'))
out=ROOT/'artifacts/repair-acceptance';out.mkdir(exist_ok=True)
rows=[]
models=[('nllb_ct2_beam1',NllbCT2(ROOT/'models/nllb-ct2'))] if 'fast' in sys.argv else [('nllb_ct2_beam3',NllbCT2(ROOT/'models/nllb-ct2'))] if 'nllb' in sys.argv else [('onnx_greedy',Marian(ROOT/'models/marian-en-hi')),('ct2_beam3',MarianCT2(ROOT/'models/marian-ct2'))]
for name,model in models:
    times=[]
    for sample in samples:
        start=time.perf_counter()
        try:actual=model.translate(sample['english'],beam_size=1) if 'fast' in sys.argv else model.translate(sample['english']);error=None
        except Exception as exc:actual='';error=str(exc)
        ms=(time.perf_counter()-start)*1000;times.append(ms)
        rows.append(dict(engine=name,**sample,actualHindi=actual,ms=ms,error=error,humanMeaningRating=None,humanNaturalnessRating=None))
    print(json.dumps(dict(engine=name,count=len(times),medianMs=statistics.median(times),p95Ms=sorted(times)[-3])),flush=True)
stem='unseen-nllb-fast' if 'fast' in sys.argv else 'unseen-nllb' if 'nllb' in sys.argv else 'unseen-mt'
(out/(stem+'.json')).write_text(json.dumps(rows,indent=2,ensure_ascii=False),encoding='utf-8')
with (out/(stem+'-human-review.csv')).open('w',encoding='utf-8-sig',newline='') as f:
    writer=csv.DictWriter(f,fieldnames=list(rows[0]));writer.writeheader();writer.writerows(rows)
