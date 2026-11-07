"""Promote all FP16 tensors/ops in the public export to FP32 arithmetic for CPU stability.
This does not recover precision lost when upstream weights were quantized to FP16.
"""
from pathlib import Path
import onnx
import numpy as np
import hashlib,json
from onnx import TensorProto,numpy_helper
ROOT=Path(__file__).resolve().parents[1]
source=ROOT/'models'/'kokoro-hf.fp16.onnx'
target=ROOT/'models'/'kokoro-cpu-fp32.onnx'
def tensor(t):
 if t.data_type==TensorProto.FLOAT16:
  t.CopyFrom(numpy_helper.from_array(numpy_helper.to_array(t).astype(np.float32),name=t.name))
def graph(g):
 for t in g.initializer:tensor(t)
 for item in list(g.input)+list(g.output)+list(g.value_info):
  if item.type.HasField('tensor_type') and item.type.tensor_type.elem_type==TensorProto.FLOAT16:item.type.tensor_type.elem_type=TensorProto.FLOAT
 for node in g.node:
  if node.op_type=='Cast':
   for attr in node.attribute:
    if attr.name=='to' and attr.i==TensorProto.FLOAT16:attr.i=TensorProto.FLOAT
  for attr in node.attribute:
   if attr.type==onnx.AttributeProto.TENSOR:tensor(attr.t)
   elif attr.type==onnx.AttributeProto.GRAPH:graph(attr.g)
if __name__=='__main__':
 if not target.exists():
  model=onnx.load(source);graph(model.graph);onnx.checker.check_model(model);onnx.save(model,target)
 print(target.name,target.stat().st_size,flush=True)
 with source.open('rb') as f: source_hash=hashlib.file_digest(f,'sha256').hexdigest()
 with target.open('rb') as f: target_hash=hashlib.file_digest(f,'sha256').hexdigest()
 (ROOT/'models'/'derived-manifest.json').write_text(json.dumps({'source':'kokoro-hf.fp16.onnx','sourceSha256':source_hash,'derived':target.name,'derivedSha256':target_hash,'change':'All FP16 tensors/value types and Cast-to-FP16 ops promoted to FP32 for CPU numerical stability. Does not recover upstream discarded precision.'},indent=2))
