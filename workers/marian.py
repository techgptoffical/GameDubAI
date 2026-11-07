"""CPU ONNX Marian encoder/decoder, local SentencePiece vocabulary, bounded greedy decoding.
No Transformers/PyTorch, remote code, hub access or token downloads at runtime.
"""
import json
import re
from pathlib import Path
import numpy as np
import onnxruntime as ort
import sentencepiece as spm

class Marian:
    def __init__(self, root: Path):
        ort.disable_telemetry_events()
        self.config=json.loads((root/'config.json').read_text())
        self.vocab=json.loads((root/'vocab.json').read_text(encoding='utf-8'))
        self.reverse={v:k for k,v in self.vocab.items()}
        self.source=spm.SentencePieceProcessor(model_file=str(root/'source.spm'))
        self.target=spm.SentencePieceProcessor(model_file=str(root/'target.spm'))
        options=ort.SessionOptions();options.intra_op_num_threads=2;options.inter_op_num_threads=1
        options.enable_cpu_mem_arena=False
        options.add_session_config_entry('session.intra_op.allow_spinning','0')
        self.encoder=ort.InferenceSession(str(root/'encoder_model_quantized.onnx'),sess_options=options,providers=['CPUExecutionProvider'])
        self.decoder=ort.InferenceSession(str(root/'decoder_model_merged_quantized.onnx'),sess_options=options,providers=['CPUExecutionProvider'])
        self.output_names=[o.name for o in self.decoder.get_outputs()]
        self.cache_inputs=[i.name for i in self.decoder.get_inputs() if i.name.startswith('past_key_values.')]

    def translate(self,text):
        # Sentence-level MT can omit a neighboring sentence if fed a paragraph. Split complete
        # sentences, retaining abbreviations used as titles. The model still has no story reasoning.
        pieces=re.split(r'(?<=[.!?])\s+(?=[A-Z\"\'])',text.strip())
        sentences=[]
        for piece in pieces:
            if sentences and re.search(r'\b(?:Mr|Mrs|Ms|Dr|St|Prof)\.$',sentences[-1]):sentences[-1]+=' '+piece
            else:sentences.append(piece)
        return ' '.join(self._sentence(s) for s in sentences if s)

    def _sentence(self,text):
        ids=[self.vocab.get(p,self.vocab.get('<unk>',1)) for p in self.source.encode(text,out_type=str)]+[self.config['eos_token_id']]
        if len(ids)>256:raise ValueError('Subtitle exceeds the local translation token limit; split it into dialogue lines')
        inputs=np.array([ids],dtype=np.int64);mask=np.ones_like(inputs)
        hidden=self.encoder.run(None,{'input_ids':inputs,'attention_mask':mask})[0]
        cache={name:np.zeros((1,8,0,64),dtype=np.float32) for name in self.cache_inputs}
        token=self.config['decoder_start_token_id'];generated=[]
        for step in range(160):
            feed={'encoder_attention_mask':mask,'encoder_hidden_states':hidden,'input_ids':np.array([[token]],dtype=np.int64),'use_cache_branch':np.array([step>0],dtype=np.bool_),**cache}
            outputs=dict(zip(self.output_names,self.decoder.run(None,feed)))
            logits=outputs['logits'][0,-1].copy();logits[self.config['pad_token_id']]=-np.inf
            token=int(np.argmax(logits))
            if token==self.config['eos_token_id']:break
            generated.append(token)
            for name in self.cache_inputs:
                present=name.replace('past_key_values.','present.')
                value=outputs[present]
                if value.size:cache[name]=value
        else:raise ValueError('Translation failed to finish within 160 tokens; dropped instead of speaking a truncated line')
        return self.target.decode([self.reverse.get(i,'<unk>') for i in generated])
