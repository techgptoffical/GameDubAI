"""Replace three unsupported depthwise transposed convolutions with equivalent
zero insertion + depthwise convolutions. Preserve the source model and weights.
"""
import hashlib,json
from pathlib import Path
import numpy as np
import onnx
from onnx import helper,numpy_helper
import onnxruntime as ort
ROOT=Path(__file__).resolve().parents[1];source=ROOT/'models/kokoro-cpu-fp32.onnx';target=ROOT/'models/kokoro-dml-fp32.onnx'
model=onnx.load(source);weights={w.name:w for w in model.graph.initializer};nodes=[];records=[]
def init(array,name):
    model.graph.initializer.append(numpy_helper.from_array(array,name));return name
for node in model.graph.node:
    attrs={a.name:helper.get_attribute_value(a) for a in node.attribute}
    group=attrs.get('group',1)
    if node.op_type!='ConvTranspose' or group==1:nodes.append(node);continue
    weight=numpy_helper.to_array(weights[node.input[1]])
    if not (weight.shape==(group,1,3) and attrs.get('strides')==[2] and attrs.get('pads')==[1,1] and attrs.get('output_padding')==[1]):
        raise RuntimeError('Unexpected depthwise transpose configuration; refusing an unsafe rewrite')
    prefix=node.name+'/compatible'
    axes=init(np.array([3],np.int64),prefix+'/axes');pads=init(np.array([0,0,0,0,0,0,0,1],np.int64),prefix+'/pads')
    shape=init(np.array([0,0,-1],np.int64),prefix+'/shape');flipped=init(weight[:,:,::-1].copy(),prefix+'/weight')
    replacements=[helper.make_node('Unsqueeze',[node.input[0],axes],[prefix+'/expanded'],name=prefix+'/expand'),
        helper.make_node('Pad',[prefix+'/expanded',pads],[prefix+'/padded'],name=prefix+'/zero_insert',mode='constant'),
        helper.make_node('Reshape',[prefix+'/padded',shape],[prefix+'/upsampled'],name=prefix+'/reshape'),
        helper.make_node('Conv',[prefix+'/upsampled',flipped,*node.input[2:]],[*node.output],name=prefix+'/conv',group=group,kernel_shape=[3],pads=[1,1],strides=[1])]
    # Validate the algebra against the original ONNX operator at several dynamic lengths.
    errors=[]
    for length in [2,7,31]:
        x=np.random.default_rng(123).normal(size=(1,group,length)).astype(np.float32)
        bias=numpy_helper.to_array(weights[node.input[2]]) if len(node.input)>2 else np.zeros(group,np.float32)
        y=np.zeros((1,group,length*2),np.float32)
        for k in range(3):
            for pos in range(length):
                dest=pos*2-1+k
                if 0<=dest<length*2:y[:,:,dest]+=x[:,:,pos]*weight[:,0,k]
        y+=bias[None,:,None]
        zeros=np.zeros((1,group,length*2),np.float32);zeros[:,:,::2]=x
        padded=np.pad(zeros,((0,0),(0,0),(1,1)))
        actual=sum(padded[:,:,k:k+length*2]*weight[:,0,2-k][None,:,None] for k in range(3))+bias[None,:,None]
        error=float(np.max(np.abs(y-actual)));errors.append(error)
        if error>1e-5:raise RuntimeError('Depthwise rewrite failed numerical validation')
    nodes.extend(replacements);records.append(dict(node=node.name,group=group,lengths=[2,7,31],maxAbsErrors=errors))
if len(records)!=3:raise RuntimeError('Expected exactly three depthwise transpose nodes')
del model.graph.node[:];model.graph.node.extend(nodes);onnx.checker.check_model(model)
onnx.save(model,target)
report=dict(source=source.name,target=target.name,sha256=hashlib.file_digest(target.open('rb'),'sha256').hexdigest(),rewrites=records,
    license='Apache-2.0 upstream model; local graph rewrite, no retraining',note='Operator algebra verified; full GPU inference and human listening are separate tests')
(ROOT/'models/kokoro-dml-manifest.json').write_text(json.dumps(report,indent=2))
print(json.dumps(report),flush=True)
