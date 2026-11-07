"""Compare bounded CPU inference options using existing models; no downloads or playback."""
import gc, json, sys, time, statistics
from pathlib import Path
import psutil
import numpy as np
import onnxruntime as ort
from kokoro_onnx import Kokoro

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'workers'))
from marian import Marian
process = psutil.Process()
process.nice(psutil.BELOW_NORMAL_PRIORITY_CLASS)
arena = sys.argv[1] == 'arena'
options = ort.SessionOptions()
options.intra_op_num_threads = 2
options.inter_op_num_threads = 1
options.enable_cpu_mem_arena = arena
options.add_session_config_entry('session.intra_op.allow_spinning', '0')
session = ort.InferenceSession(str(ROOT/'models/kokoro-cpu-fp32.onnx'), sess_options=options, providers=['CPUExecutionProvider'])
model = Kokoro.from_session(session, str(ROOT/'models/voices-v1.0.bin'))
samples = ['हमें यहाँ से निकलना होगा!', 'दरवाजा खोलो।', 'मुझे लगा था तुम लौट आओगे।', 'हमें यहाँ से निकलना होगा!']
records = []
for text in samples:
    start = time.perf_counter()
    audio, rate = model.create(text, voice='hf_alpha', lang='hi')
    valid = bool(np.isfinite(audio).all() and np.max(np.abs(audio)) > 0.00001)
    row = dict(ms=(time.perf_counter()-start)*1000, audioSeconds=len(audio)/rate, valid=valid, rssMiB=process.memory_info().rss/1048576)
    records.append(row)
    print(json.dumps(row), flush=True)
summary = dict(arena=arena, threads=2, medianMs=statistics.median(r['ms'] for r in records), records=records,
    note='Live system load varies. No game FPS or naturalness score is inferred. No audio playback.')
out = ROOT/'artifacts/model-evaluation'/('live-cpu-arena.json' if arena else 'live-cpu-no-arena.json')
out.write_text(json.dumps(summary, indent=2), encoding='utf-8')
print(json.dumps({'file': str(out), 'medianMs': summary['medianMs']}), flush=True)
