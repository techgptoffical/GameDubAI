"""Indic Parler-TTS (Apache-2.0, ai4bharat) on the RTX GPU: the four named Hindi speakers on real
conversational lines from the translation benchmark. Real audio, real timings, VRAM peak.
Usage: runtime-torch\\Scripts\\python.exe scripts\\benchmark_parler_voices.py"""
import json, time, sys, warnings
from pathlib import Path
import numpy as np, soundfile as sf, torch
warnings.filterwarnings('ignore')
ROOT = Path(__file__).resolve().parents[1]
from parler_tts import ParlerTTSForConditionalGeneration
from transformers import AutoTokenizer

MODEL = ROOT / 'models/indic-parler-tts'
out = ROOT / 'artifacts/voice-benchmark'; out.mkdir(parents=True, exist_ok=True)
dev = 'cuda'
t0 = time.perf_counter()
model = ParlerTTSForConditionalGeneration.from_pretrained(MODEL, torch_dtype=torch.float16).to(dev).eval()
tok = AutoTokenizer.from_pretrained(MODEL)
desc_tok = AutoTokenizer.from_pretrained(model.config.text_encoder._name_or_path)
load_s = time.perf_counter() - t0
# Voice profiles: one fixed named speaker each (consistency), delivery from available evidence.
PROFILES = {
    'male_deep':    "Rohit's voice is deep and natural, with a calm, moderate pace and clear, close-up recording quality.",
    'male_young':   "Aman speaks with energy and a slightly fast pace, expressive and lively, in a clear close-up recording.",
    'female_expr':  "Divya's voice is natural and expressive with a moderate pace, in a clear close-up recording.",
    'female_calm':  "Rani speaks in a mature, calm and warm tone at a moderate pace, in a clear close-up recording.",
}
URGENT = " The delivery is urgent and intense."
rows = {r['Id']: r for r in json.loads((ROOT / 'artifacts/translation-benchmark/conversational-v6.json').read_text(encoding='utf-8'))['rows']}
tests = [('u05', 'male_young', True), ('u14', 'male_deep', False), ('u21', 'female_expr', True), ('u23', 'male_deep', False),
         ('u49', 'male_young', False), ('u58', 'female_calm', False), ('u22', 'female_expr', True), ('u38', 'male_deep', False)]

def synth(text, description):
    d = desc_tok(description, return_tensors='pt').to(dev); p = tok(text, return_tensors='pt').to(dev)
    with torch.inference_mode():
        g = model.generate(input_ids=d.input_ids, attention_mask=d.attention_mask, prompt_input_ids=p.input_ids, prompt_attention_mask=p.attention_mask, do_sample=True, temperature=0.8)
    return g.float().cpu().numpy().squeeze()

synth('ठीक है।', PROFILES['male_deep'])  # warm-up (kernels)
torch.cuda.reset_peak_memory_stats()
report = []
for id, profile, urgent in tests:
    text = rows[id]['conversational']
    torch.cuda.synchronize(); s = time.perf_counter()
    audio = synth(text, PROFILES[profile] + (URGENT if urgent else ''))
    torch.cuda.synchronize(); ms = (time.perf_counter() - s) * 1000
    rate = model.config.sampling_rate; secs = len(audio) / rate
    name = f'parler-{id}-{profile}.wav'; sf.write(out / name, audio, rate)
    report.append(dict(file=name, english=rows[id]['English'], hindi=text, profile=profile, generationMs=round(ms), audioSeconds=round(secs, 2), realTimeFactor=round(ms / 1000 / secs, 2)))
    print(name, f'{ms:.0f} ms for {secs:.1f} s audio (RTF {ms/1000/secs:.2f})', flush=True)
summary = dict(engine='indic-parler-tts fp16 CUDA', loadSeconds=round(load_s, 1), peakVramMiB=round(torch.cuda.max_memory_allocated() / 1048576),
               medianRTF=float(np.median([r['realTimeFactor'] for r in report])), samples=report,
               note='Real generated audio. No human MOS ratings; listen to the WAVs.')
(out / 'parler.json').write_text(json.dumps(summary, ensure_ascii=False, indent=1), encoding='utf-8')
print('load', summary['loadSeconds'], 's; peak VRAM', summary['peakVramMiB'], 'MiB; median RTF', summary['medianRTF'])
