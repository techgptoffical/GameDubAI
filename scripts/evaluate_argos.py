"""Keep the rejected baseline reproducible; it is not the selected application translator."""
import json,time
from pathlib import Path
import ctranslate2,sentencepiece
root=Path(__file__).resolve().parents[1]
model=root/'models'/'translation'/'en_hi'
translator=ctranslate2.Translator(str(model/'model'),device='cpu',compute_type='int8',intra_threads=2,inter_threads=1)
tokenizer=sentencepiece.SentencePieceProcessor(model_file=str(model/'sentencepiece.model'))
records=[]
for sample in json.loads((root/'data'/'dialogue-tests.json').read_text(encoding='utf-8')):
 t=time.perf_counter();out=translator.translate_batch([tokenizer.encode(sample['english'],out_type=str)],beam_size=3,replace_unknowns=True)[0]
 records.append({'id':sample['id'],'english':sample['english'],'referenceHindi':sample['hindi'],'modelHindi':tokenizer.decode(out.hypotheses[0]),'ms':(time.perf_counter()-t)*1000})
(root/'artifacts'/'argos-evaluation.json').write_text(json.dumps(records,indent=2,ensure_ascii=False),encoding='utf-8')
print('Saved 32 actual Argos baseline translations')
