"""Unseen game-dialogue benchmark: current sentence MT (Marian, NLLB) with and without the old
word-replacement layer. Outputs actual model Hindi for human review; no reference translations are
used by the app. Usage: runtime\\python.exe scripts\\benchmark_conversational_mt.py"""
import json, statistics, sys, time
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]; sys.path.insert(0, str(ROOT / 'workers'))
from marian_ct2 import MarianCT2
from nllb_ct2 import NllbCT2

lines = json.loads((ROOT / 'research/translation-benchmark/lines.json').read_text(encoding='utf-8'))
out = ROOT / 'artifacts/translation-benchmark'; out.mkdir(parents=True, exist_ok=True)
# Tatsam/formal markers that rarely occur in everyday spoken Hindi (evaluation heuristic only).
FORMAL = ['कृपया','प्रस्थान','स्थान','सहायता','शीघ्र','आवश्यक','प्रतीक्षा','कार्य','अत्यंत','वास्तव','प्रयास','निश्चित','किंतु','परंतु',
          'अतः','एवं','तथा','यद्यपि','मानसिक','श्रीमान','क्षमा','धन्यवाद','उपरांत','पश्चात','अनुमति','संभवतः','केवल','अभी तक','आपको','आपका','आप']
def formal_hits(h): return [w for w in FORMAL if w in h]
results = {}
for name, model, kwargs in [('marian', MarianCT2(ROOT / 'models/marian-ct2'), {}), ('nllb', NllbCT2(ROOT / 'models/nllb-ct2'), {'beam_size': 3})]:
    rows = []; times = []
    for line in lines:
        start = time.perf_counter(); hindi = model.translate(line['english'], **kwargs); ms = (time.perf_counter() - start) * 1000
        times.append(ms); rows.append(dict(id=line['id'], english=line['english'], hindi=hindi, ms=round(ms, 1), formal=formal_hits(hindi)))
    results[name] = dict(medianMs=statistics.median(times), p95Ms=sorted(times)[int(len(times) * .95) - 1], formalLines=sum(1 for r in rows if r['formal']), rows=rows)
    print(name, 'median', round(results[name]['medianMs']), 'ms; lines with formal markers', results[name]['formalLines'], '/', len(rows), flush=True)
(out / 'baseline-mt.json').write_text(json.dumps(results, ensure_ascii=False, indent=1), encoding='utf-8')
