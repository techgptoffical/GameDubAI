"""IndicF5 (MIT, ai4bharat) on the RTX GPU, voice-prompted by licensed synthetic Indic Parler-TTS
speakers (Apache-2.0), so each profile is a consistent synthetic voice, not a cloned real person.
Step 1 (once): Parler generates a ~6-8 s reference per profile -> models/voice-profiles/.
Step 2: IndicF5 speaks the benchmark lines with each profile; real timings and audio.
Usage: runtime-torch\\Scripts\\python.exe scripts\\benchmark_indicf5_voices.py"""
import json, sys, time, types, warnings
from pathlib import Path
import numpy as np, soundfile as sf, torch
warnings.filterwarnings('ignore')
ROOT = Path(__file__).resolve().parents[1]
REF = ROOT / 'models/voice-profiles'; REF.mkdir(parents=True, exist_ok=True)
out = ROOT / 'artifacts/voice-benchmark'; out.mkdir(parents=True, exist_ok=True)
PROFILES = {
    'male_deep':   ("Rohit's voice is deep and natural, with a calm, moderate pace and clear, close-up recording quality.", 'आज रात का काम बहुत ध्यान से करना होगा, एक गलती और सब खत्म।'),
    'male_young':  ("Aman speaks with energy and a slightly fast pace, expressive and lively, in a clear close-up recording.", 'अरे यार, चल ना जल्दी! गाड़ी बाहर खड़ी है, आज मज़ा आएगा।'),
    'female_expr': ("Divya's voice is natural and expressive with a moderate pace, in a clear close-up recording.", 'तुमने वादा किया था कि सब ठीक हो जाएगा, पर देखो हम फिर वहीं हैं।'),
    'female_calm': ("Rani speaks in a mature, calm and warm tone at a moderate pace, in a clear close-up recording.", 'घबराओ मत, आराम से बैठो और बताओ कि हुआ क्या है।'),
}
if not all((REF / f'{p}.wav').exists() for p in PROFILES):
    from parler_tts import ParlerTTSForConditionalGeneration
    from transformers import AutoTokenizer
    M = ROOT / 'models/indic-parler-tts'
    pm = ParlerTTSForConditionalGeneration.from_pretrained(M, torch_dtype=torch.float16).to('cuda').eval()
    tok = AutoTokenizer.from_pretrained(M); dtok = AutoTokenizer.from_pretrained(pm.config.text_encoder._name_or_path)
    torch.manual_seed(7)
    for name, (desc, text) in PROFILES.items():
        d = dtok(desc, return_tensors='pt').to('cuda'); p = tok(text, return_tensors='pt').to('cuda')
        for seed in range(8):
            torch.manual_seed(seed)
            with torch.inference_mode():
                a = pm.generate(input_ids=d.input_ids, attention_mask=d.attention_mask, prompt_input_ids=p.input_ids, prompt_attention_mask=p.attention_mask, do_sample=True, temperature=0.7).float().cpu().numpy().squeeze()
            secs = len(a) / pm.config.sampling_rate
            if len(text) / 16 <= secs <= len(text) / 9: break
        sf.write(REF / f'{name}.wav', a, pm.config.sampling_rate); (REF / f'{name}.txt').write_text(text, encoding='utf-8')
        print('reference', name, round(len(a) / pm.config.sampling_rate, 1), 's', flush=True)
    (REF / 'LICENSE.txt').write_text('Synthetic reference voices generated locally with ai4bharat/indic-parler-tts (Apache-2.0) named speakers Rohit, Aman, Divya, Rani. Not recordings of real people.\n', encoding='utf-8')
    del pm; torch.cuda.empty_cache()

import importlib.util
if importlib.util.find_spec('wandb') is None:  # f5_tts trainer-only import; never used at inference
    import importlib.machinery; _w = types.ModuleType('wandb'); _w.__spec__ = importlib.machinery.ModuleSpec('wandb', None); sys.modules['wandb'] = _w
import torchaudio
def _load(path, *a, **k):  # torchaudio 2.9+ needs torchcodec for load(); soundfile is enough for WAV
    data, sr = sf.read(str(path), dtype='float32', always_2d=True); return torch.from_numpy(data.T.copy()), sr
torchaudio.load = _load
from f5_tts.infer.utils_infer import load_vocoder, load_model, preprocess_ref_audio_text, infer_process
from f5_tts.model import DiT
from safetensors.torch import load_file
dev = 'cuda'; t0 = time.perf_counter()
vocoder = load_vocoder(vocoder_name='vocos', is_local=False, device=dev)
model = load_model(DiT, dict(dim=1024, depth=22, heads=16, ff_mult=2, text_dim=512, conv_layers=4), mel_spec_type='vocos', vocab_file=str(ROOT / 'models/indicf5/checkpoints/vocab.txt'), device=dev)
state = load_file(str(ROOT / 'models/indicf5/model.safetensors'), device=dev)
model.load_state_dict({k.removeprefix('ema_model._orig_mod.'): v for k, v in state.items() if k.startswith('ema_model.')}, strict=True)
vocoder.load_state_dict({k.removeprefix('vocoder._orig_mod.'): v for k, v in state.items() if k.startswith('vocoder.')}, strict=True)
model.eval(); vocoder.eval(); load_s = time.perf_counter() - t0
refs = {p: preprocess_ref_audio_text(str(REF / f'{p}.wav'), (REF / f'{p}.txt').read_text(encoding='utf-8'), show_info=lambda *a: None) for p in PROFILES}
rows = {r['Id']: r for r in json.loads((ROOT / 'artifacts/translation-benchmark/conversational-v6.json').read_text(encoding='utf-8'))['rows']}
tests = [('u05', 'male_young'), ('u14', 'male_deep'), ('u21', 'female_expr'), ('u23', 'male_deep'), ('u49', 'male_young'), ('u58', 'female_calm'), ('u22', 'female_expr'), ('u38', 'male_deep')]
def synth(text, profile, nfe):
    ra, rt = refs[profile]
    with torch.inference_mode():
        audio, sr, _ = infer_process(ra, rt, text, model, vocoder, mel_spec_type='vocos', nfe_step=nfe, speed=1.0, device=dev, show_info=lambda *a: None, progress=None)
    return audio, sr
synth('ठीक है।', 'male_deep', 16)
torch.cuda.reset_peak_memory_stats(); report = []
for nfe in (16, 8):
    for id, profile in tests:
        text = rows[id]['conversational']; torch.cuda.synchronize(); s = time.perf_counter()
        audio, sr = synth(text, profile, nfe); torch.cuda.synchronize(); ms = (time.perf_counter() - s) * 1000
        secs = len(audio) / sr; name = f'indicf5-nfe{nfe}-{id}-{profile}.wav'; sf.write(out / name, audio, sr)
        report.append(dict(file=name, nfe=nfe, english=rows[id]['English'], hindi=text, profile=profile, generationMs=round(ms), audioSeconds=round(secs, 2), realTimeFactor=round(ms / 1000 / secs, 3)))
        print(name, f'{ms:.0f} ms for {secs:.1f} s (RTF {ms/1000/secs:.2f})', flush=True)
summary = dict(engine='IndicF5 fp32 CUDA, Parler synthetic references', loadSeconds=round(load_s, 1), peakVramMiB=round(torch.cuda.max_memory_allocated() / 1048576),
               medianRTF={n: float(np.median([r['realTimeFactor'] for r in report if r['nfe'] == n])) for n in (16, 8)}, samples=report, note='Real generated audio; no human MOS ratings.')
(out / 'indicf5.json').write_text(json.dumps(summary, ensure_ascii=False, indent=1), encoding='utf-8')
print('load', summary['loadSeconds'], 's; peak VRAM', summary['peakVramMiB'], 'MiB; median RTF', summary['medianRTF'])
