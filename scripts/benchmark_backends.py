import faulthandler
faulthandler.enable()
import sys,time,json
from pathlib import Path
import onnxruntime as rt
from kokoro_onnx import Kokoro
print("providers", rt.get_available_providers(), flush=True)
backend=sys.argv[1] if len(sys.argv)>1 else "cpu"
model=sys.argv[2] if len(sys.argv)>2 else "kokoro-hf.fp16.onnx"
options=rt.SessionOptions();options.enable_mem_pattern=False;options.intra_op_num_threads=2;options.inter_op_num_threads=1;options.execution_mode=rt.ExecutionMode.ORT_SEQUENTIAL
providers=[("DmlExecutionProvider",{"device_id":"0"}),"CPUExecutionProvider"] if backend=="dml" else ["CPUExecutionProvider"]
print("loading",model,backend,flush=True)
t=time.perf_counter()
session=rt.InferenceSession(str(Path('models')/model),sess_options=options,providers=providers)
print("loaded",time.perf_counter()-t,session.get_providers(),flush=True)
k=Kokoro.from_session(session,'models/voices-v1.0.bin')
records=[]
for voice in ['hf_alpha','hm_omega','hf_alpha']:
 t=time.perf_counter()
 audio,rate=k.create('हमें यहाँ से निकलना होगा!',voice=voice,lang='hi')
 record={"voice":voice,"seconds":time.perf_counter()-t,"audioSeconds":len(audio)/rate}
 records.append(record); print(json.dumps(record),flush=True)
Path('artifacts').mkdir(exist_ok=True)
Path(f'artifacts/backend-{backend}-{model}.json').write_text(json.dumps(records,indent=2))
