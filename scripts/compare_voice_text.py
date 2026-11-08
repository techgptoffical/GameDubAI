"""Real Kokoro Hindi audio for the same English line rendered from old Marian text vs the new
conversational text (same voice, same settings), so phrasing effects on naturalness can be heard.
Usage: runtime\\python.exe scripts\\compare_voice_text.py  (reads artifacts/translation-benchmark/conversational-v6.json)"""
import json, sys, wave
from pathlib import Path
import numpy as np
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'workers')); sys.argv = ['x', 'tts']
from local_worker import Models
rows = {r['Id']: r for r in json.loads((ROOT / 'artifacts/translation-benchmark/conversational-v6.json').read_text(encoding='utf-8'))['rows']}
out = ROOT / 'artifacts/voice-comparison'; out.mkdir(parents=True, exist_ok=True)
model = Models('tts'); report = []
for id, voice in [('u05', 'hm_omega'), ('u14', 'hm_omega'), ('u21', 'hf_beta'), ('u23', 'hm_psi'), ('u49', 'hm_psi'), ('u58', 'hf_beta')]:
    r = rows[id]
    for kind in ('marian', 'conversational'):
        res = model.run({'text': r[kind], 'voice': voice, 'speed': 1.0, 'sentencePause': .2})
        pcm = np.frombuffer(__import__('base64').b64decode(res['pcm']), dtype='<i2')
        name = f'{id}-{voice}-{kind}.wav'
        with wave.open(str(out / name), 'wb') as w: w.setnchannels(1); w.setsampwidth(2); w.setframerate(res['rate']); w.writeframes(pcm.tobytes())
        report.append(dict(file=name, english=r['English'], hindi=r[kind], seconds=round(res['durationSeconds'], 2), inferenceMs=round(res['inferenceMs'])))
        print(name, report[-1]['seconds'], 's', flush=True)
(out / 'comparison.json').write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding='utf-8')
