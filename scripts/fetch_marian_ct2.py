"""Download pinned, Apache-2.0 Marian data files only; never imports remote code."""
import hashlib,json,shutil,urllib.request
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
DEST=ROOT/'models/marian-ct2'; DEST.mkdir(exist_ok=True)
REPO='manancode/opus-mt-en-hi-ctranslate2-android'
REV='17cce1395c7b9f75e07a0778cfd00c982a79ec53'
FILES={'model.bin':(77981115,'29c1207b3ee52185d4e2cc8333d7a1d9b277b103747b9e0c1efbfc29828f6250'),
 'source.spm':(812240,'fd4e951487aed00bae6a6c2ee4ef5d8d1db05fd098b19b608046c9334b58d24d'),
 'target.spm':(1067935,'f5529d3a72f8c1d5f7e357f1b6fd30e3cf58f6e1ba0401db135a118ac92f4a76'),
 'config.json':(223,None),'shared_vocabulary.json':(1799187,None),'README.md':(1253,None)}
if shutil.disk_usage(ROOT).free<1024**3:raise RuntimeError('Keep at least 1 GiB free before download')
manifest=[]
for name,(size,digest) in FILES.items():
    path=DEST/name; url=f'https://huggingface.co/{REPO}/resolve/{REV}/{name}?download=true'
    if not path.exists():
        partial=path.with_suffix(path.suffix+'.partial')
        with urllib.request.urlopen(url,timeout=45) as source,partial.open('wb') as target:
            total=0
            while chunk:=source.read(1024*1024):
                total+=len(chunk)
                if total>size:raise RuntimeError('Download exceeded declared size')
                target.write(chunk)
        if partial.stat().st_size!=size:raise RuntimeError('Incomplete model download')
        partial.replace(path)
    actual=hashlib.file_digest(path.open('rb'),'sha256').hexdigest()
    if digest and actual!=digest:raise RuntimeError('Checksum mismatch for '+name)
    manifest.append(dict(file=name,bytes=path.stat().st_size,sha256=actual,url=url))
    print(name,path.stat().st_size,flush=True)
(DEST/'manifest.json').write_text(json.dumps(dict(repo=REPO,revision=REV,license='Apache-2.0',upstream='Helsinki-NLP/opus-mt-en-hi',files=manifest),indent=2))
