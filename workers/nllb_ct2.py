"""Local NLLB 600M INT8, English to Hindi. CC-BY-NC-4.0 model restrictions apply."""
import re
import ctranslate2
import sentencepiece as spm

class NllbCT2:
    def __init__(self,root):
        self.engine=ctranslate2.Translator(str(root),device='cpu',compute_type='int8',intra_threads=2,inter_threads=1)
        self.tokenizer=spm.SentencePieceProcessor(model_file=str(root/'sentencepiece.bpe.model'))
    def translate(self,text,beam_size=3):
        sentences=re.split(r'(?<=[.!?])\s+(?=[A-Z\"\'])',text.strip())
        pieces=[]
        for s in sentences:
            if pieces and re.search(r'\b(?:Mr|Mrs|Ms|Dr|St|Prof)\.$',pieces[-1]):pieces[-1]+=' '+s
            else:pieces.append(s)
        sources=[]
        for sentence in pieces:
            tokens=self.tokenizer.encode(sentence,out_type=str)
            if len(tokens)>256:raise ValueError('Subtitle exceeds 256 source tokens')
            sources.append(['eng_Latn']+tokens+['</s>'])
        results=self.engine.translate_batch(sources,target_prefix=[['hin_Deva'] for _ in sources],beam_size=max(1,min(3,int(beam_size))),max_decoding_length=160,max_input_length=0,return_end_token=True)
        output=[]
        for result in results:
            tokens=result.hypotheses[0]
            if tokens[-1]!='</s>':raise ValueError('Translation did not finish; no truncated dialogue was spoken')
            output.append(self.tokenizer.decode([t for t in tokens[:-1] if t!='hin_Deva']))
        return ' '.join(output)
