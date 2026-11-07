"""Verify the local archive and launch it from a fresh project-owned extraction."""
from pathlib import Path
import tempfile,zipfile,subprocess,json,hashlib,shutil,time
ROOT=Path(__file__).resolve().parents[1]
archive=ROOT/'artifacts'/'GameDubAI-Local-Windows.zip'
check=Path(tempfile.mkdtemp(prefix='package-check-',dir=ROOT/'artifacts'))
try:
 with zipfile.ZipFile(archive) as bundle:
  for item in bundle.infolist():
   if not (check/item.filename).resolve().is_relative_to(check.resolve()):raise RuntimeError('Archive path escaped validation folder')
  broken=bundle.testzip()
  if broken:raise RuntimeError('Archive CRC failed: '+broken)
  print('Archive CRC passed; extracting local validation copy',flush=True)
  bundle.extractall(check)
 startup=subprocess.STARTUPINFO();startup.dwFlags|=subprocess.STARTF_USESHOWWINDOW;startup.wShowWindow=0
 app=subprocess.Popen([str(check/'build'/'GameDubAI'/'GameDub.App.exe'),'--smoke-test'],cwd=check,startupinfo=startup,creationflags=subprocess.CREATE_NO_WINDOW)
 try:exit_code=app.wait(timeout=45)
 except subprocess.TimeoutExpired:app.kill();app.wait();raise RuntimeError('Extracted app did not shut down within 45 seconds')
 health=json.loads((check/'local-settings'/'startup-health.json').read_text(encoding='utf-8'))
 if exit_code!=0 or not health.get('ready') or health.get('runtime')!='bundled Python':raise RuntimeError('Extracted application/model startup failed')
 with archive.open('rb') as source:digest=hashlib.file_digest(source,'sha256').hexdigest()
 report={'archive':archive.name,'bytes':archive.stat().st_size,'sha256':digest,'crcPassed':True,'freshExtractionAppExit':exit_code,'modelsReady':health['ready'],'runtime':health['runtime'],'validatedOn':'development Windows 11 PC','internetRequiredAtRuntime':False,'note':'No fresh-OS or real-game validation. App loaded models and exited; no UI automation or screen recording.'}
 (ROOT/'artifacts'/'package-verification.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
 print(json.dumps(report,indent=2),flush=True)
finally:
 # Only remove the unique temporary tree created above, after verifying its absolute boundary.
 if check.resolve().parent!=(ROOT/'artifacts').resolve() or not check.name.startswith('package-check-'):raise RuntimeError('Refusing cleanup outside the owned validation directory')
 for attempt in range(3):
  try:shutil.rmtree(check);break
  except PermissionError:
   if attempt==2:raise
   time.sleep(.3)
