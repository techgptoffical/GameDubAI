from pathlib import Path
import subprocess,json,time,base64
ROOT=Path(__file__).resolve().parents[1]
python=ROOT/'runtime'/'python.exe'
worker=ROOT/'workers'/'local_worker.py'
records=[]
requests={'translate':{'text':'The bridge is broken.'},'tts':{'text':'हमें यहाँ से निकलना होगा!','voice':'hf_alpha'},'asr':{'pcm':base64.b64encode(bytes(32000)).decode()}}
for kind,payload in requests.items():
 start=time.perf_counter()
 process=subprocess.run([str(python),'-u',str(worker),kind],input=json.dumps({'op':'warmup'})+'\n'+json.dumps(payload)+'\n',capture_output=True,text=True,encoding='utf-8',cwd=ROOT,timeout=60,creationflags=subprocess.CREATE_NO_WINDOW)
 lines=[json.loads(line) for line in process.stdout.splitlines()]
 passed=process.returncode==0 and len(lines)==2 and all(item.get('ok') for item in lines)
 if kind=='tts' and passed:passed=len(base64.b64decode(lines[1].get('pcm','')))>10000 and lines[1]['rate']==24000
 if kind=='asr' and passed:passed=lines[1].get('text')==''
 record={'kind':kind,'passed':passed,'exitCode':process.returncode,'seconds':time.perf_counter()-start}
 records.append(record);print(json.dumps(record),flush=True)
 if not passed:raise RuntimeError('Bundled runtime verification failed: '+kind)
try:
 import sys
 sys.path.insert(0,str(ROOT/'workers'))
 import local_worker,socket
 try:
  socket.create_connection(('127.0.0.1',9))
  raise RuntimeError('Offline network guard did not reject a connection')
 except PermissionError:records.append({'kind':'offline-network-guard','passed':True})
finally:
 (ROOT/'artifacts'/'runtime-verification.json').write_text(json.dumps(records,indent=2),encoding='utf-8')
