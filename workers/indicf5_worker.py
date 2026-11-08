"""Persistent IndicF5 (MIT, ai4bharat) Hindi voice worker on the GPU; newline JSON over stdin/stdout,
same protocol as local_worker.py TTS. Four consistent synthetic voices, prompted by references made
with Indic Parler-TTS named speakers (Apache-2.0) in models/voice-profiles. Offline only.
Run with runtime-torch\\Scripts\\python.exe (PyTorch CUDA)."""
import base64, contextlib, importlib.machinery, json, os, sys, time, types
from pathlib import Path
os.environ['HF_HUB_OFFLINE'] = '1'; os.environ['HF_HUB_DISABLE_TELEMETRY'] = '1'; os.environ['OMP_NUM_THREADS'] = '2'
ROOT = Path(__file__).resolve().parents[1]
sys.stdout.reconfigure(encoding='utf-8'); sys.stdin.reconfigure(encoding='utf-8')
def offline_audit(event, args):
    if event == 'socket.connect': raise PermissionError('GameDub voice worker is offline-only.')
sys.addaudithook(offline_audit)
# Same mapping the app uses for character voices: Kokoro voice IDs keep each character's identity.
VOICES = {'hm_omega': 'male_deep', 'hm_psi': 'male_young', 'hf_alpha': 'female_expr', 'hf_beta': 'female_calm',
          'male_deep': 'male_deep', 'male_young': 'male_young', 'female_expr': 'female_expr', 'female_calm': 'female_calm'}

class F5:
    def __init__(self):
        with contextlib.redirect_stdout(sys.stderr):
            import numpy as np, soundfile as sf, torch, torchaudio
            if importlib.util.find_spec('wandb') is None:  # trainer-only import in f5_tts; unused at inference
                w = types.ModuleType('wandb'); w.__spec__ = importlib.machinery.ModuleSpec('wandb', None); sys.modules['wandb'] = w
            def load(path, *a, **k):  # torchaudio 2.9+ needs torchcodec for load(); WAV via soundfile
                data, sr = sf.read(str(path), dtype='float32', always_2d=True); return torch.from_numpy(data.T.copy()), sr
            torchaudio.load = load
            from f5_tts.infer.utils_infer import load_model, preprocess_ref_audio_text, infer_process
            from f5_tts.model import DiT
            from safetensors.torch import load_file
            from vocos import Vocos
            self.np, self.torch, self.infer = np, torch, infer_process
            torch.set_num_threads(2)
            dev = 'cuda' if torch.cuda.is_available() else 'cpu'
            dev = 'cuda' if torch.cuda.is_available() else 'cpu'
            state = load_file(str(ROOT / 'models/indicf5/model.safetensors'), device=dev)  # straight to VRAM, no RAM copy
            self.vocoder = Vocos.from_hparams(str(ROOT / 'models/indicf5-vocos/config.yaml'))
            self.vocoder.load_state_dict({k.removeprefix('vocoder._orig_mod.'): v for k, v in state.items() if k.startswith('vocoder.')}); self.vocoder.eval().to(dev)
            self.model = load_model(DiT, dict(dim=1024, depth=22, heads=16, ff_mult=2, text_dim=512, conv_layers=4), mel_spec_type='vocos',
                                    vocab_file=str(ROOT / 'models/indicf5/checkpoints/vocab.txt'), device=dev)
            self.model.load_state_dict({k.removeprefix('ema_model._orig_mod.'): v for k, v in state.items() if k.startswith('ema_model.')}); self.model.eval()
            del state; import gc; gc.collect()
            if dev == 'cuda': torch.cuda.empty_cache()
            refs = ROOT / 'models/voice-profiles'
            self.refs = {p: preprocess_ref_audio_text(str(refs / f'{p}.wav'), (refs / f'{p}.txt').read_text(encoding='utf-8'), show_info=lambda *a: None)
                         for p in set(VOICES.values())}
            self.dev = dev
    def speak(self, text, voice, speed, steps):
        ra, rt = self.refs[VOICES.get(voice, 'male_deep')]
        with contextlib.redirect_stdout(sys.stderr), self.torch.inference_mode():
            audio, rate, _ = self.infer(ra, rt, text, self.model, self.vocoder, mel_spec_type='vocos', nfe_step=steps, speed=speed,
                                        device=self.dev, show_info=lambda *a: None, progress=None)
        return self.np.asarray(audio, dtype='float32'), rate

def main():
    model = None
    for raw in sys.stdin:
        start = time.perf_counter()
        try:
            request = json.loads(raw)
            if model is None: model = F5()
            if request.get('op') == 'warmup':
                model.speak('ठीक है।', 'hm_omega', 1.0, 8); result = {'ok': True, 'kind': 'tts-f5'}
            else:
                text = request.get('text', '').strip()
                if not text or len(text) > 600: raise ValueError('Text must contain 1-600 characters')
                speed = max(.9, min(1.15, float(request.get('speed', 1.0)))); steps = int(max(6, min(32, request.get('steps', 8))))
                samples, rate = model.speak(text, request.get('voice', 'hm_omega'), speed, steps)
                np = model.np
                if not np.isfinite(samples).all() or samples.size < rate // 20: raise ValueError('Voice produced empty or invalid audio')
                peak = float(np.max(np.abs(samples))); samples = np.clip(samples * min(4.0, .9 / max(peak, 1e-5)), -.98, .98)
                pcm = (samples * 32767).astype('<i2').tobytes()
                result = {'ok': True, 'pcm': base64.b64encode(pcm).decode('ascii'), 'rate': rate, 'durationSeconds': len(samples) / rate,
                          'peak': peak, 'inferenceMs': (time.perf_counter() - start) * 1000}
        except Exception as exc:
            result = {'ok': False, 'error': f'Local tts-f5 worker: {type(exc).__name__}: {exc}'}
        print(json.dumps(result, ensure_ascii=True), flush=True)

if __name__ == '__main__':
    import importlib.util
    main()
