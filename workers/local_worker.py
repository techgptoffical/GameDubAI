"""One persistent, CPU-only offline model per process; newline JSON over private stdin/stdout.

No listening network port, microphone, credentials, or downloads. Host discards obsolete replies.
"""
import base64
import contextlib
import json
import os
import sys
import time
from pathlib import Path

os.environ["OMP_NUM_THREADS"] = "2"
os.environ["OPENBLAS_NUM_THREADS"] = "1"
os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
ROOT = Path(__file__).resolve().parents[1]
sys.stdout.reconfigure(encoding="utf-8")
sys.stdin.reconfigure(encoding="utf-8")
def offline_audit(event, args):
    if event == "socket.connect":
        raise PermissionError("GameDub model workers are offline-only; network connections are disabled.")
sys.addaudithook(offline_audit)

class Models:
    def __init__(self, kind):
        self.kind = kind
        with contextlib.redirect_stdout(sys.stderr):
            if kind == "translate":
                from marian import Marian
                self.marian = Marian(ROOT / "models" / "marian-en-hi")
            elif kind == "tts":
                import onnxruntime as ort
                ort.disable_telemetry_events()
                from kokoro_onnx import Kokoro
                options = ort.SessionOptions()
                options.intra_op_num_threads = 2
                options.inter_op_num_threads = 1
                options.enable_cpu_mem_arena = False
                options.add_session_config_entry("session.intra_op.allow_spinning", "0")
                session = ort.InferenceSession(str(ROOT / "models" / "kokoro-cpu-fp32.onnx"), sess_options=options, providers=["CPUExecutionProvider"])
                self.kokoro = Kokoro.from_session(session, str(ROOT / "models" / "voices-v1.0.bin"))
            elif kind == "asr":
                import vosk
                vosk.SetLogLevel(-1)
                self.asr_model = vosk.Model(str(ROOT / "models" / "vosk-model-small-en-us-0.15"))
                self.recognizer = vosk.KaldiRecognizer(self.asr_model, 16000)
                self.recognizer.SetWords(True)
            else:
                raise ValueError("Unknown worker kind")

    def run(self, request):
        start = time.perf_counter()
        if request.get("op") == "warmup":
            return {"ok": True, "kind": self.kind}
        if self.kind == "asr":
            if request.get("op") == "reset":
                self.recognizer.Reset()
                return {"ok": True, "text": ""}
            pcm = base64.b64decode(request.get("pcm", ""), validate=True)
            if len(pcm) > 16000 * 2 * 5:
                raise ValueError("ASR chunk exceeds five seconds")
            if self.recognizer.AcceptWaveform(pcm):
                result = json.loads(self.recognizer.Result())
                words = result.get("result", [])
                confidence = sum(w.get("conf", 0) for w in words)/len(words) if words else 0
                return {"ok": True, "text": result.get("text", ""), "confidence": confidence}
            return {"ok": True, "text": ""}
        text = request.get("text", "").strip()
        if not text or len(text) > 2000:
            raise ValueError("Text must contain 1-2000 characters")
        with contextlib.redirect_stdout(sys.stderr):
            if self.kind == "translate":
                output = self.marian.translate(text)
                return {"ok": True, "hindi": output, "inferenceMs": (time.perf_counter()-start)*1000}
            import numpy as np
            voice = request.get("voice", "hf_alpha")
            if voice not in ("hf_alpha", "hf_beta", "hm_omega", "hm_psi"):
                raise ValueError("Select one of the four bundled Hindi voice IDs")
            speed = max(.92, min(1.08, float(request.get("speed", 1.0))))
            samples, rate = self.kokoro.create(text.replace("।", "."), voice=voice, speed=speed, lang="hi")
            samples = np.nan_to_num(samples, nan=0.0, posinf=0.0, neginf=0.0)
            peak = float(np.max(np.abs(samples))) if samples.size else 0
            if samples.size < rate // 20 or peak < .00001:
                raise ValueError("TTS produced empty or silent audio")
            samples = np.clip(samples * min(1.0, .95/max(peak,.00001)), -.98, .98)
            pcm = (samples * 32767).astype("<i2").tobytes()
            return {"ok": True, "pcm": base64.b64encode(pcm).decode("ascii"), "rate": rate,
                    "durationSeconds": len(samples)/rate, "peak": peak, "inferenceMs": (time.perf_counter()-start)*1000}

def main():
    model = None
    kind = sys.argv[1]
    for raw in sys.stdin:
        try:
            request = json.loads(raw)
            if model is None:
                model = Models(kind)
            result = model.run(request)
        except Exception as exc:
            # No environment variables or command lines in errors.
            result = {"ok": False, "error": f"Local {kind} worker: {type(exc).__name__}: {exc}"}
        print(json.dumps(result, ensure_ascii=True), flush=True)

if __name__ == "__main__":
    main()
