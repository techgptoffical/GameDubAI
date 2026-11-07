"""Resumable bounded range downloader for a public Apache-2.0 Kokoro conversion."""
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
import urllib.request
import time
ROOT = Path(__file__).resolve().parents[1]
TARGET = ROOT / "models" / "kokoro-hf.fp16.onnx.download"
TOTAL = 163234740
URL = "https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/main/onnx/model_fp16.onnx?download=true"

def part(bounds):
    start, end = bounds
    for retry in range(3):
        try:
            request = urllib.request.Request(URL + f"&part={start}", headers={"Range": f"bytes={start}-{end}", "Accept-Encoding": "identity"})
            with urllib.request.urlopen(request, timeout=30) as source:
                if source.status != 206 or not source.headers.get("Content-Range", "").startswith(f"bytes {start}-"):
                    raise RuntimeError("Server did not honor the bounded byte range")
                length = 0
                with TARGET.open("r+b") as out:
                    out.seek(start)
                    while length < end-start+1:
                        block = source.read(min(256*1024, end-start+1-length))
                        if not block: break
                        out.write(block); length += len(block)
                if length != end-start+1: raise RuntimeError("Truncated model range")
            print(f"range {start}-{end} complete", flush=True)
            return
        except Exception:
            if retry == 2: raise
            time.sleep(1)

if __name__ == "__main__":
    offset = TARGET.stat().st_size if TARGET.exists() else 0
    # Only resume the existing contiguous prefix on the first invocation. A failed parallel run
    # uses the saved prefix marker rather than treating sparse file size as completed bytes.
    marker = TARGET.with_suffix(".prefix")
    if marker.exists(): offset = int(marker.read_text())
    else: marker.write_text(str(offset))
    if not TARGET.exists(): TARGET.touch()
    ranges = [(s, min(s+4*1024*1024-1, TOTAL-1)) for s in range(offset, TOTAL, 4*1024*1024)]
    with ThreadPoolExecutor(max_workers=6) as pool: list(pool.map(part, ranges))
    if TARGET.stat().st_size != TOTAL: raise RuntimeError("Incorrect model size")
    TARGET.replace(ROOT / "models" / "kokoro-hf.fp16.onnx")
    marker.unlink()
    print("FP16 download complete", flush=True)
