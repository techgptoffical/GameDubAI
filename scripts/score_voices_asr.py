"""Objective intelligibility check: transcribe every engine's Hindi WAV with Whisper large-v3
(faster-whisper, local) and compute character error rate against the text it was asked to speak.
Lower CER = clearer pronunciation. Not a naturalness score; listen for that.
Usage: runtime-torch\\Scripts\\python.exe scripts\\score_voices_asr.py"""
import json, os, re, unicodedata
from pathlib import Path
import torch, soundfile as sf, librosa
os.add_dll_directory(str(Path(torch.__file__).parent / 'lib'))  # cuBLAS/cuDNN for CTranslate2
from faster_whisper import WhisperModel
ROOT = Path(__file__).resolve().parents[1]
snap = ROOT / 'models/whisper-large-v3'  # Systran/faster-whisper-large-v3 (MIT), evaluation only
asr = WhisperModel(str(snap), device='cuda', compute_type='float16')
def norm(s):
    s = unicodedata.normalize('NFC', s).replace('़', '')  # ignore nukta differences
    return re.sub(r'[^ऀ-ॣ०-ॿ]', '', s)
def cer(ref, hyp):
    r, h = norm(ref), norm(hyp); prev = list(range(len(h) + 1))
    for i in range(1, len(r) + 1):
        cur = [i] + [0] * len(h)
        for j in range(1, len(h) + 1): cur[j] = min(cur[j - 1] + 1, prev[j] + 1, prev[j - 1] + (r[i - 1] != h[j - 1]))
        prev = cur
    return prev[-1] / max(1, len(r))
texts = {r['Id']: r['conversational'] for r in json.loads((ROOT / 'artifacts/translation-benchmark/conversational-v6.json').read_text(encoding='utf-8'))['rows']}
ids = ['u05', 'u14', 'u21', 'u23', 'u49', 'u58']
engines = {
    'kokoro': lambda i: next((ROOT / 'artifacts/voice-comparison').glob(f'{i}-*-conversational.wav'), None),
    'parler': lambda i: next((ROOT / 'artifacts/voice-benchmark').glob(f'parler-{i}-*.wav'), None),
    'indicf5-nfe16': lambda i: next((ROOT / 'artifacts/voice-benchmark').glob(f'indicf5-nfe16-{i}-*.wav'), None),
    'indicf5-nfe8': lambda i: next((ROOT / 'artifacts/voice-benchmark').glob(f'indicf5-nfe8-{i}-*.wav'), None),
}
result = {}
for engine, find in engines.items():
    rows = []
    for i in ids:
        f = find(i)
        if f is None: continue
        wav, sr = sf.read(str(f), dtype='float32', always_2d=True); wav = librosa.resample(wav.mean(1), orig_sr=sr, target_sr=16000)
        segs, _ = asr.transcribe(wav, language='hi', beam_size=5, vad_filter=False)
        heard = ''.join(s.text for s in segs).strip()
        rows.append(dict(id=i, file=f.name, spoken=texts[i], heard=heard, cer=round(cer(texts[i], heard), 3)))
    result[engine] = dict(meanCER=round(sum(r['cer'] for r in rows) / max(1, len(rows)), 3), rows=rows)
    print(engine, 'mean CER', result[engine]['meanCER'], [r['cer'] for r in rows], flush=True)
(ROOT / 'artifacts/voice-benchmark/asr-cer.json').write_text(json.dumps(result, ensure_ascii=False, indent=1), encoding='utf-8')
