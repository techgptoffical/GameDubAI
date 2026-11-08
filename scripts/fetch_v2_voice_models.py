"""Pinned public model data only. No credentials, accounts, executable remote code, or billing.

Piper: two ~64 MB Hindi research voices, CC-BY-NC-SA-4.0 dataset restrictions.
Chatterbox: optional single Q4 ONNX set ~1.55 GB, MIT, builtin distributed reference.
"""
import argparse,hashlib,json,shutil,urllib.request
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser();parser.add_argument('--chatterbox',action='store_true');args=parser.parse_args()
if shutil.disk_usage(ROOT).free<6*1024**3:raise RuntimeError('Keep at least 6 GiB free for development/gameplay')

def fetch(repo,revision,folder,names,dest,license_name):
    api=f'https://huggingface.co/api/models/{repo}/tree/{revision}'+('/'+folder if folder else '')
    metadata=json.load(urllib.request.urlopen(api,timeout=30));rows=[];dest.mkdir(parents=True,exist_ok=True)
    for name in names:
        remote=(folder+'/' if folder else '')+name
        entry=next(e for e in metadata if e['path']==remote)
        path=dest/name;url=f'https://huggingface.co/{repo}/resolve/{revision}/{remote}?download=true'
        if not path.exists():
            partial=path.with_suffix(path.suffix+'.partial');size=0;last=0
            with urllib.request.urlopen(url,timeout=60) as source,partial.open('wb') as out:
                while block:=source.read(1024**2):
                    size+=len(block)
                    if size>entry['size']:raise RuntimeError('Oversized download '+name)
                    out.write(block)
                    if size-last>64*1024**2:print(name,round(size/1024**2),'MiB',flush=True);last=size
            if size!=entry['size']:raise RuntimeError('Incomplete download '+name)
            partial.replace(path)
        data=path.read_bytes()
        if len(data)!=entry['size']:raise RuntimeError('Wrong size '+name)
        digest=hashlib.sha256(data).hexdigest()
        if 'lfs' in entry:
            if digest!=entry['lfs']['oid']:raise RuntimeError('SHA256 mismatch '+name)
        elif hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()!=entry['oid']:
            raise RuntimeError('Git blob mismatch '+name)
        rows.append(dict(file=name,url=url,bytes=len(data),sha256=digest));print('Verified',name,len(data),flush=True)
    manifest=dest/'manifest.json'
    previous=json.loads(manifest.read_text())['files'] if manifest.exists() else []
    (dest/'manifest.json').write_text(json.dumps(dict(repo=repo,revision=revision,license=license_name,files=previous+rows),indent=2))

if args.chatterbox:
    repo='onnx-community/chatterbox-multilingual-ONNX';revision='452d3f434aa592098f1eedac9099f33642ab2da5';dest=ROOT/'models/chatterbox-onnx'
    fetch(repo,revision,'',['README.md','tokenizer.json','tokenizer_config.json','default_voice.wav'],dest,'MIT')
    fetch(repo,revision,'onnx',['speech_encoder.onnx','speech_encoder.onnx_data','embed_tokens.onnx','embed_tokens.onnx_data','conditional_decoder.onnx','conditional_decoder.onnx_data','language_model_q4.onnx','language_model_q4.onnx_data'],dest/'onnx','MIT')
else:
    for voice in ['pratham','priyamvada']:
        name='hi_IN-'+voice+'-medium.onnx'
        fetch('rhasspy/piper-voices','e7229f7d33a10e145e109b886d661984201ac3c4','hi/hi_IN/'+voice+'/medium',[name,name+'.json','MODEL_CARD'],ROOT/'models/piper'/voice,'Model card dataset: CC-BY-NC-SA-4.0; research/noncommercial use only')
