"""Optional noncommercial research translation model; ~630 MB, pinned data only."""
import hashlib,json,shutil,urllib.request
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1];DEST=ROOT/'models/nllb-ct2';DEST.mkdir(exist_ok=True)
REPO='JustFrederik/nllb-200-distilled-600M-ct2-int8';REV='302d78f00e6fdb50a1064059df7c392b735e9d05'
FILES={'model.bin':(622595991,'ed1beaf75134de7505315a5223162f56acff397eff6b50638a500d3936fe707b'),
 'sentencepiece.bpe.model':(4852054,'14bb8dfb35c0ffdea7bc01e56cea38b9e3d5efcdcb9c251d6b40538e1aab555a'),
 'config.json':(159,None),'shared_vocabulary.txt':(2568098,None),'README.md':(3435,None)}
if shutil.disk_usage(ROOT).free<3*1024**3:raise RuntimeError('Keep 3 GiB free before this optional download')
manifest=[]
for name,(size,digest) in FILES.items():
    path=DEST/name;url=f'https://huggingface.co/{REPO}/resolve/{REV}/{name}?download=true'
    if not path.exists():
        partial=path.with_suffix(path.suffix+'.partial')
        with urllib.request.urlopen(url,timeout=45) as source,partial.open('wb') as target:
            total=0;last=0
            while chunk:=source.read(1024*1024):
                total+=len(chunk)
                if total>size:raise RuntimeError('Download exceeded declared size')
                target.write(chunk)
                if total-last>=64*1024**2:print(name,round(total/1024**2),'MiB',flush=True);last=total
        if partial.stat().st_size!=size:raise RuntimeError('Incomplete model download')
        partial.replace(path)
    actual=hashlib.file_digest(path.open('rb'),'sha256').hexdigest()
    if digest and actual!=digest:raise RuntimeError('Checksum mismatch for '+name)
    manifest.append(dict(file=name,bytes=path.stat().st_size,sha256=actual,url=url))
    print(name,path.stat().st_size,flush=True)
(DEST/'manifest.json').write_text(json.dumps(dict(repo=REPO,revision=REV,license='CC-BY-NC-4.0',upstream='facebook/nllb-200-distilled-600M',restriction='Noncommercial only; upstream research model, not certified translation',files=manifest),indent=2))
for name,url in [('UPSTREAM_MODEL_CARD.md','https://huggingface.co/facebook/nllb-200-distilled-600M/raw/main/README.md'),('LICENSE.txt','https://creativecommons.org/licenses/by-nc/4.0/legalcode.txt')]:
    try:(DEST/name).write_bytes(urllib.request.urlopen(url,timeout=20).read())
    except Exception as e:print('Notice fetch:',type(e).__name__,flush=True)
