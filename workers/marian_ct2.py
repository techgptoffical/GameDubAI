"""Pinned local Marian weights, CTranslate2 INT8, bounded beam decoding; no remote code."""
import re
from pathlib import Path
import ctranslate2
import sentencepiece as spm

class MarianCT2:
    def __init__(self,root:Path,beam_size=3):
        self.engine=ctranslate2.Translator(str(root),device='cpu',compute_type='int8',intra_threads=2,inter_threads=1)
        self.source=spm.SentencePieceProcessor(model_file=str(root/'source.spm'))
        self.target=spm.SentencePieceProcessor(model_file=str(root/'target.spm'))
        self.beam_size=beam_size
    def translate(self,text):
        pieces=re.split(r'(?<=[.!?])\s+(?=[A-Z\"\'])',text.strip())
        sentences=[]
        for piece in pieces:
            if sentences and re.search(r'\b(?:Mr|Mrs|Ms|Dr|St|Prof)\.$',sentences[-1]):sentences[-1]+=' '+piece
            else:sentences.append(piece)
        inputs=[]
        for sentence in sentences:
            tokens=self.source.encode(sentence,out_type=str)
            if len(tokens)>256:raise ValueError('Subtitle exceeds 256 source tokens')
            inputs.append(tokens+['</s>'])
        results=self.engine.translate_batch(inputs,beam_size=self.beam_size,max_decoding_length=160,max_input_length=0,return_end_token=True)
        output=[]
        for result in results:
            tokens=result.hypotheses[0]
            if not tokens or tokens[-1]!='</s>':raise ValueError('Translation did not finish; no truncated dialogue was spoken')
            output.append(self.target.decode(tokens[:-1]))
        return ' '.join(output)
