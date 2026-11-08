"""Local Q4 Chatterbox Hindi evaluation adapter, no downloads or remote code.

Inference adapted from the MIT onnx-community/chatterbox-multilingual-ONNX
model card, revision 452d3f434aa592098f1eedac9099f33642ab2da5.
Original conversion/inference: Vladislav Bronzov, Xenova, Resemble AI.
Only the model-distributed builtin reference is used; no personal voice cloning.
"""
import time
from pathlib import Path
import numpy as np
import onnxruntime as ort
from tokenizers import Tokenizer
import soundfile as sf

class ChatterboxOnnx:
    def __init__(self,directory,backend='cpu'):
        directory=Path(directory);options=ort.SessionOptions()
        options.intra_op_num_threads=2;options.inter_op_num_threads=1
        options.enable_cpu_mem_arena=False;options.enable_mem_pattern=False
        options.execution_mode=ort.ExecutionMode.ORT_SEQUENTIAL
        options.add_session_config_entry('session.intra_op.allow_spinning','0')
        ort.disable_telemetry_events()
        def load(name):
            print('Loading quality model:',name,flush=True)
            providers=['CPUExecutionProvider']
            if backend=='dml' and name!='language_model_q4.onnx':providers=[('DmlExecutionProvider',{'device_id':0}),'CPUExecutionProvider']
            session=ort.InferenceSession(str(directory/'onnx'/name),sess_options=options,providers=providers)
            print('Loaded:',name,flush=True);return session
        self.encoder=load('speech_encoder.onnx');self.embed=load('embed_tokens.onnx')
        self.language=load('language_model_q4.onnx');self.decoder=load('conditional_decoder.onnx')
        self.tokenizer=Tokenizer.from_file(str(directory/'tokenizer.json'))
        audio,rate=sf.read(directory/'default_voice.wav',dtype='float32')
        if rate!=24000 or audio.ndim!=1:raise ValueError('Expected builtin mono 24kHz reference')
        self.condition,self.prompt,self.vector,self.features=self.encoder.run(None,{'audio_values':audio[None,:]})
    def synthesize(self,text,exaggeration=.5,max_tokens=320,timeout=150):
        if not text.strip() or len(text)>500:raise ValueError('Use a short nonempty delivery chunk, at most 500 characters')
        text=text.replace('।','.')
        ids=np.array([self.tokenizer.encode('[hi]'+text).ids],dtype=np.int64)
        positions=np.where(ids>=6561,0,np.arange(ids.shape[1])[None,:]-1).astype(np.int64)
        inputs={'input_ids':ids,'position_ids':positions,'exaggeration':np.array([exaggeration],dtype=np.float32)}
        generated=np.array([[6561]],dtype=np.int64);started=time.monotonic();ended=False
        for index in range(max_tokens):
            if time.monotonic()-started>timeout:raise TimeoutError('Quality voice exceeded its evaluation time limit')
            embeds=self.embed.run(None,inputs)[0]
            if index==0:
                embeds=np.concatenate((self.condition,embeds),axis=1)
                batch,length,_=embeds.shape
                past={f'past_key_values.{layer}.{kv}':np.zeros([batch,16,0,64],dtype=np.float32) for layer in range(30) for kv in ('key','value')}
                mask=np.ones((batch,length),dtype=np.int64)
            logits,*present=self.language.run(None,dict(inputs_embeds=embeds,attention_mask=mask,**past))
            logits=logits[:,-1,:];scores=np.take_along_axis(logits,generated,axis=1)
            np.put_along_axis(logits,generated,np.where(scores<0,scores*1.2,scores/1.2),axis=1)
            token=np.argmax(logits,axis=-1,keepdims=True).astype(np.int64)
            generated=np.concatenate((generated,token),axis=1)
            if (token==6562).all():ended=True;break
            inputs['input_ids']=token;inputs['position_ids']=np.full((batch,1),index+1,dtype=np.int64)
            mask=np.concatenate((mask,np.ones((batch,1),dtype=np.int64)),axis=1)
            for key,value in zip(past,present):past[key]=value
        if not ended:raise RuntimeError('Quality voice did not finish within its token limit; incomplete audio rejected')
        speech=np.concatenate((self.prompt,generated[:,1:-1]),axis=1)
        audio=self.decoder.run(None,dict(speech_tokens=speech,speaker_embeddings=self.vector,speaker_features=self.features))[0].reshape(-1)
        if not len(audio) or not np.isfinite(audio).all() or np.max(np.abs(audio))<1e-5:raise RuntimeError('Invalid or silent quality voice output')
        return audio,24000,dict(tokens=index+1,generationSeconds=time.monotonic()-started,streaming=False,reference='model-distributed default_voice.wav',exaggeration=exaggeration)
