"""Actual alternative-model audio and timings; listening ratings deliberately unset."""
import argparse,base64,json,os,sys,time,wave
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
sys.path[:0]=[str(ROOT/'runtime-voiceextras'),str(ROOT/'runtime-piper'),str(ROOT/'workers')]
os.environ['OMP_NUM_THREADS']='2';os.environ['OPENBLAS_NUM_THREADS']='1'
import numpy as np,psutil
process=psutil.Process();process.nice(psutil.BELOW_NORMAL_PRIORITY_CLASS)
def deny_network(event,args):
    if event=='socket.connect':raise PermissionError('Offline voice benchmark: no network connections')
sys.addaudithook(deny_network)
parser=argparse.ArgumentParser();parser.add_argument('--chatterbox',action='store_true');parser.add_argument('--kokoro',action='store_true');parser.add_argument('--gpu',action='store_true');args=parser.parse_args()
out=ROOT/'artifacts/v2-voices';out.mkdir(parents=True,exist_ok=True)
lines=[('calm','तुम आ गए। मुझे लगा था अब हम कभी नहीं मिलेंगे।'),('urgent','जल्दी करो! वे लोग किसी भी वक्त यहाँ आ सकते हैं।'),('sad','मैंने उस पर भरोसा किया था, और उसने मुझे धोखा दिया।'),('question','माइकल, क्या तुम सच में वहाँ अकेले जाने वाले हो?'),('sarcasm','वाह, कमाल का प्लान है। अब हम सब फँस गए हैं।')]
rows=[];before=psutil.virtual_memory().available;start=time.perf_counter()
try:
    if args.kokoro:
        from local_worker import Models
        model=Models('tts-dml' if args.gpu else 'tts');model.run(dict(op='warmup'));load=time.perf_counter()-start
        voices=['hm_omega','hm_psi','hf_alpha','hf_beta'];engine='Kokoro FP32 '+('DML' if args.gpu else 'CPU2')
        def synth(text,voice,category):
            result=model.run(dict(text=text,voice=voice,speed=1,sentencePause=.2))
            return np.frombuffer(base64.b64decode(result['pcm']),dtype='<i2').astype('float32')/32767,result['rate'],dict(streaming=False,exaggeration=None)
    elif args.chatterbox:
        from chatterbox_onnx import ChatterboxOnnx
        model=ChatterboxOnnx(ROOT/'models/chatterbox-onnx');load=time.perf_counter()-start
        voices=['builtin'];engine='Chatterbox Multilingual ONNX Q4 CPU2'
        def synth(text,voice,category):return model.synthesize(text,exaggeration=.7 if category=='urgent' else .5)
    else:
        import onnxruntime as ort
        from piper import PiperVoice
        from piper.config import PiperConfig
        options=ort.SessionOptions();options.intra_op_num_threads=2;options.inter_op_num_threads=1
        options.add_session_config_entry('session.intra_op.allow_spinning','0');ort.disable_telemetry_events()
        models={}
        for name in ['pratham','priyamvada']:
            path=ROOT/'models/piper'/name/('hi_IN-'+name+'-medium.onnx')
            config=PiperConfig.from_dict(json.loads(path.with_suffix('.onnx.json').read_text()))
            models[name]=PiperVoice(session=ort.InferenceSession(str(path),sess_options=options,providers=['CPUExecutionProvider']),config=config)
        load=time.perf_counter()-start;voices=list(models);engine='Piper 1.8 medium CPU2'
        def synth(text,voice,category):
            chunks=list(models[voice].synthesize(text));audio=np.concatenate([c.audio_float_array for c in chunks]);return audio,chunks[0].sample_rate,dict(streaming='sentence chunks supported; this benchmark collects the full utterance',exaggeration=None)
    for category,text in lines:
        for voice in voices:
            started=time.perf_counter();audio,rate,details=synth(text,voice,category);ms=(time.perf_counter()-started)*1000
            if not np.isfinite(audio).all() or not len(audio):raise RuntimeError('Nonfinite or empty samples')
            file=('kokoro-' if args.kokoro else 'chatterbox-' if args.chatterbox else 'piper-')+voice+'-'+category+'.wav'
            with wave.open(str(out/file),'wb') as target:
                target.setnchannels(1);target.setsampwidth(2);target.setframerate(rate);target.writeframes((np.clip(audio,-1,1)*32767).astype('<i2').tobytes())
            row=dict(file=file,text=text,category=category,voice=voice,fullGenerationMs=ms,audioSeconds=len(audio)/rate,rssMiB=process.memory_info().rss/1048576,naturalness=None,pronunciation=None,expressiveness=None,details=details)
            rows.append(row);print(file,round(ms),'ms',flush=True)
    report=dict(engine=engine,loadSeconds=load,availableRamBeforeMiB=before/1048576,samples=rows,note='Actual generated WAVs, not real-game acceptance. Category describes the text, not a proven delivered emotion. Human listening ratings remain unset.')
except Exception as error:
    report=dict(error=repr(error),samples=rows,availableRamBeforeMiB=before/1048576)
    raise
finally:
    (out/('kokoro-'+('gpu' if args.gpu else 'cpu')+'.json' if args.kokoro else 'chatterbox.json' if args.chatterbox else 'piper.json')).write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
