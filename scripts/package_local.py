"""Create a local offline bundle. Never uploads or publishes anything."""
import json,zipfile,hashlib
import psutil
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
OUT=ROOT/'artifacts'/'GameDubAI-Local-Windows.zip'
psutil.Process().nice(psutil.BELOW_NORMAL_PRIORITY_CLASS)
app_dir='build/GameDubAI-next' if (ROOT/'build'/'GameDubAI-next'/'GameDub.App.exe').exists() else 'build/GameDubAI'
folders=[app_dir,'runtime','workers','models/marian-en-hi','models/vosk-model-small-en-us-0.15','data','profiles','licenses','src','tests','tools','scripts','research','artifacts/model-evaluation']
files=['Run GameDub.cmd','Setup.ps1','Test.ps1','Smoke-Test.ps1','requirements.lock.txt','GameDubAI.sln','Directory.Build.props','appsettings.example.json','README.md','IMPLEMENTATION_PLAN.md','TEST_RESULTS.md','PERFORMANCE_REPORT.md','FINAL_DEVELOPMENT_REPORT.md','THIRD_PARTY_NOTICES.md','LICENSE','models/kokoro-cpu-fp32.onnx','models/voices-v1.0.bin','models/manifest.json','models/derived-manifest.json','artifacts/app-smoke.json']
files += ['WINDOW_SELECTION_FIX.md','SILENCE_FIX.md','artifacts/window-fix-smoke.json','artifacts/target-selection-smoke.json','artifacts/game-window-diagnostics.json','artifacts/runtime-verification.json']
required=[app_dir+'/GameDub.App.exe','runtime/python.exe','models/kokoro-cpu-fp32.onnx','models/marian-en-hi/encoder_model_quantized.onnx']
for name in required:
 if not (ROOT/name).exists():raise RuntimeError('Missing runnable bundle component: '+name)
paths={ROOT/name for name in files if (ROOT/name).is_file()}
for folder in folders:
 exclude={'__pycache__'} | ({'bin','obj'} if folder in ['src','tests','tools'] else set())
 for path in (ROOT/folder).rglob('*'):
  if path.is_file() and not exclude.intersection(path.relative_to(ROOT/folder).parts):paths.add(path)
runs=sorted((ROOT/'artifacts'/'tests').glob('*/results.json'))
selected_runs={runs[-1].parent}
integration_runs=[r.parent for r in runs if (r.parent/'neural-latency.json').exists() and (r.parent/'asr-pipeline.json').exists()]
if integration_runs:selected_runs.add(integration_runs[-1])
for run in selected_runs:
 for path in run.glob('*.json'):paths.add(path)
for name in ['marian-evaluation.json','argos-evaluation.json']:
 if (ROOT/'artifacts'/name).exists():paths.add(ROOT/'artifacts'/name)
with zipfile.ZipFile(OUT,'w',compression=zipfile.ZIP_DEFLATED,compresslevel=3,allowZip64=True) as bundle:
 for path in sorted(paths):
  relative=path.relative_to(ROOT).as_posix()
  if relative.startswith(app_dir+'/'):relative='build/GameDubAI/'+relative[len(app_dir)+1:]
  bundle.write(path,relative)
with OUT.open('rb') as source:digest=hashlib.file_digest(source,'sha256').hexdigest()
(OUT.with_suffix('.sha256')).write_text(digest+'  '+OUT.name+'\n')
print(json.dumps({'file':str(OUT),'bytes':OUT.stat().st_size,'files':len(paths),'sha256':digest}),flush=True)
