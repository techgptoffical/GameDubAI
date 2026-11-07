from pathlib import Path
from importlib import metadata
import json,shutil
ROOT=Path(__file__).resolve().parents[1]
DEST=ROOT/'licenses'/'python';DEST.mkdir(parents=True,exist_ok=True)
items=[]
for dist in sorted(metadata.distributions(),key=lambda d:d.metadata.get('Name','').lower()):
 name=dist.metadata.get('Name','unknown');license_name=dist.metadata.get('License-Expression') or dist.metadata.get('License') or '; '.join(c for c in dist.metadata.get_all('Classifier',[]) if c.startswith('License ::'))
 item={'name':name,'version':dist.version,'declaredLicense':license_name,'homePage':dist.metadata.get('Home-page'),'projectUrls':dist.metadata.get_all('Project-URL',[]),'licenseFiles':[]}
 overrides={'espeakng-loader':('MIT wrapper; bundled eSpeak NG is GPL-3.0','https://github.com/thewh1teagle/espeakng-loader'),'kokoro-onnx':('MIT','https://github.com/thewh1teagle/kokoro-onnx'),'vosk':('Apache-2.0','https://github.com/alphacep/vosk-api')}
 if name.lower() in overrides:
  item['verifiedUpstreamLicense'],item['verifiedUpstreamSource']=overrides[name.lower()]
 for entry in dist.files or []:
  text=str(entry).replace('\\','/')
  if '.dist-info/' not in text or not any(word in entry.name.lower() for word in ['license','copying','notice']):continue
  source=Path(dist.locate_file(entry))
  if not source.is_file():continue
  target=DEST/name/str(entry).split('.dist-info/',1)[-1]
  target.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(source,target)
  item['licenseFiles'].append(str(target.relative_to(ROOT)))
 items.append(item)
(ROOT/'licenses'/'python-dependencies.json').write_text(json.dumps(items,ensure_ascii=False,indent=2),encoding='utf-8')
print('Recorded license metadata and notices for',len(items),'Python distributions')
