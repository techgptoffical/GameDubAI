"""Isolated GPU retest with promoted FP32 graph; does not replace the CPU runtime."""
import json,sys,time
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'runtime-dml'))
import onnxruntime as ort
import numpy as np
import psutil
from kokoro_onnx import Kokoro
process=psutil.Process();process.nice(psutil.BELOW_NORMAL_PRIORITY_CLASS)
records=[]
try:
    options=ort.SessionOptions();options.enable_mem_pattern=False;options.execution_mode=ort.ExecutionMode.ORT_SEQUENTIAL
    options.intra_op_num_threads=2;options.inter_op_num_threads=1;options.add_session_config_entry('session.intra_op.allow_spinning','0')
    start=time.perf_counter()
    model='kokoro-dml-fp32.onnx' if '--patched' in sys.argv else 'kokoro-cpu-fp32.onnx'
    session=ort.InferenceSession(str(ROOT/'models'/model),sess_options=options,providers=[('DmlExecutionProvider',{'device_id':0}),'CPUExecutionProvider'])
    print('loaded',session.get_providers(),round(time.perf_counter()-start,2),flush=True)
    kokoro=Kokoro.from_session(session,str(ROOT/'models/voices-v1.0.bin'))
    lines=json.loads((ROOT/'data/acceptance-dialogue.json').read_text(encoding='utf-8'))
    for sample in [lines[i] for i in [1,8,19,32,48]]:
        for voice in ['hf_alpha','hm_omega']:
            start=time.perf_counter();audio,rate=kokoro.create(sample['referenceHindi'],voice=voice,lang='hi')
            record=dict(id=sample['id'],voice=voice,ms=(time.perf_counter()-start)*1000,seconds=len(audio)/rate,valid=bool(np.isfinite(audio).all() and np.max(np.abs(audio))>.00001),rssMiB=process.memory_info().rss/1048576)
            records.append(record);print(json.dumps(record),flush=True)
    report=dict(success=all(r['valid'] for r in records),providers=session.get_providers(),records=records,note='GPU-only inference timing, not FPS impact or acoustic playback; active gameplay must be tested separately')
except Exception as e:
    report=dict(success=False,error=str(e),records=records)
    print(type(e).__name__,str(e)[:1000],flush=True)
out=ROOT/'artifacts/repair-acceptance'/('dml-patched.json' if '--patched' in sys.argv else 'dml-retest.json');out.parent.mkdir(exist_ok=True)
out.write_text(json.dumps(report,indent=2),encoding='utf-8')
sys.exit(0 if report['success'] else 1)
