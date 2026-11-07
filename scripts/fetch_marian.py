from pathlib import Path
from concurrent.futures import ThreadPoolExecutor
import urllib.request,json,hashlib
import fetch_fp16 as fetch
ROOT=Path(__file__).resolve().parents[1]
DEST=ROOT/'models'/'marian-en-hi';DEST.mkdir(exist_ok=True)
BASE='https://huggingface.co/Xenova/opus-mt-en-hi/resolve/main/'
FILES={'encoder_model_quantized.onnx':('onnx/encoder_model_quantized.onnx',51337630),'decoder_model_merged_quantized.onnx':('onnx/decoder_model_merged_quantized.onnx',58638488)}
manifest=[]
for name,(remote,total) in FILES.items():
 target=DEST/name
 if not target.exists():
  fetch.TARGET=target.with_suffix('.partial');fetch.TARGET.touch(exist_ok=True);fetch.URL=BASE+remote+'?download=true';fetch.TOTAL=total
  ranges=[(s,min(s+4*1024*1024-1,total-1)) for s in range(0,total,4*1024*1024)]
  with ThreadPoolExecutor(max_workers=6) as pool:list(pool.map(fetch.part,ranges))
  if fetch.TARGET.stat().st_size!=total:raise RuntimeError('Incorrect model size')
  fetch.TARGET.replace(target)
 manifest.append({'file':name,'url':BASE+remote,'bytes':total,'sha256':hashlib.file_digest(target.open('rb'),'sha256').hexdigest()})
for name in ['config.json','generation_config.json','tokenizer.json','tokenizer_config.json','source.spm','target.spm','vocab.json','README.md']:
 target=DEST/name
 if not target.exists():
  with urllib.request.urlopen(BASE+name,timeout=30) as src:target.write_bytes(src.read())
(DEST/'manifest.json').write_text(json.dumps(manifest,indent=2))
print('Marian ONNX model ready',flush=True)
