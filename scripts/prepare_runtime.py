"""Bundle an isolated, project-local Python runtime; never installs system software."""
from pathlib import Path
import urllib.request,zipfile,hashlib,shutil,json
ROOT=Path(__file__).resolve().parents[1]
archive=ROOT/'artifacts'/'python-3.12.10-embed-amd64.zip'
url='https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip'
if not archive.exists():
 with urllib.request.urlopen(url,timeout=30) as source,archive.open('wb') as out:
  while block:=source.read(1024*1024):out.write(block)
# Digest published on the official Python release page. SHA256 is also recorded locally.
if hashlib.md5(archive.read_bytes()).hexdigest()!='fe8ef205f2e9c3ba44d0cf9954e1abd3':raise RuntimeError('Python archive failed upstream digest check')
runtime=ROOT/'runtime';runtime.mkdir(exist_ok=True)
with zipfile.ZipFile(archive) as package:
 for item in package.infolist():
  if not (runtime/item.filename).resolve().is_relative_to(runtime.resolve()):raise RuntimeError('Unsafe runtime archive')
 package.extractall(runtime)
destination=runtime/'Lib'/'site-packages'
if destination.exists():
 if destination.resolve()!=destination.absolute() or not destination.resolve().is_relative_to(ROOT):raise RuntimeError('Refusing to replace a linked/outside-workspace runtime path')
 shutil.rmtree(destination)
shutil.copytree(ROOT/'.venv'/'Lib'/'site-packages',destination,ignore=shutil.ignore_patterns('__pycache__','pip','pip-*.dist-info'))
(runtime/'python312._pth').write_text('python312.zip\n.\nLib/site-packages\n../workers\nimport site\n',encoding='utf-8')
(runtime/'runtime-manifest.json').write_text(json.dumps({'python':'3.12.10','url':url,'sha256':hashlib.file_digest(archive.open('rb'),'sha256').hexdigest(),'isolation':'python312._pth, project-local packages, no user-site paths'},indent=2))
print('Local embedded runtime ready',flush=True)
