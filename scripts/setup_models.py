"""Public, unauthenticated model downloads. No credential access, paid APIs or remote code execution."""
import hashlib
import json
import shutil
import urllib.request
import zipfile
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MODELS = ROOT / "models"
LIMIT = 512 * 1024 * 1024
SOURCES = {
    "kokoro-hf.fp16.onnx": "https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/main/onnx/model_fp16.onnx?download=true",
    "voices-v1.0.bin": "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.1/voices-v1.0.bin",
    "vosk-model-small-en-us-0.15.zip": "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip",
}
EXPECTED = {
    "kokoro-hf.fp16.onnx": "ba4527a874b42b21e35f468c10d326fdff3c7fc8cac1f85e9eb6c0dfc35c334a",
    "voices-v1.0.bin": "bca610b8308e8d99f32e6fe4197e7ec01679264efed0cac9140fe9c29f1fbf7d",
    "vosk-model-small-en-us-0.15.zip": "30f26242c4eb449f948e42cb302dd7a686cb29a3423a8367f99ff41780942498",
    "translate-en_hi-1_1.argosmodel": "60470a003a9c7339db8c060ed8eafc7cf999ba90dfa5dceebd8d0a4f75f1d3f0"
}

def download(name, url):
    path = MODELS / name
    if not path.exists():
        partial = path.with_suffix(path.suffix + ".partial")
        request = urllib.request.Request(url, headers={"User-Agent": "GameDubAI-local-setup/1.0"})
        with urllib.request.urlopen(request, timeout=60) as source, partial.open("wb") as target:
            declared = int(source.headers.get("Content-Length", "0"))
            if declared > LIMIT:
                raise RuntimeError(f"Download exceeds 512 MiB safety bound: {name}")
            total = 0
            while block := source.read(1024 * 1024):
                total += len(block)
                if total > LIMIT:
                    raise RuntimeError(f"Download exceeds safety bound: {name}")
                target.write(block)
        partial.replace(path)
    with path.open("rb") as source: digest = hashlib.file_digest(source, "sha256").hexdigest()
    if name in EXPECTED and digest != EXPECTED[name]: raise RuntimeError(f"Model checksum mismatch: {name}")
    return {"file": name, "url": url, "bytes": path.stat().st_size, "sha256": digest}

def main():
    MODELS.mkdir(exist_ok=True)
    if shutil.disk_usage(ROOT).free < 2 * 1024**3:
        raise RuntimeError("Keep at least 2 GiB free before setting up local models.")
    manifest = []
    if "--with-argos" in sys.argv:
        SOURCES["translate-en_hi-1_1.argosmodel"] = "https://argos-net.com/v1/translate-en_hi-1_1.argosmodel"
    for name, url in SOURCES.items():
        print(f"Preparing {name}", flush=True)
        manifest.append(download(name, url))
    archive = MODELS / "translate-en_hi-1_1.argosmodel"
    if archive.exists():
        with zipfile.ZipFile(archive) as package:
            for item in package.infolist():
                destination = (MODELS / "translation" / item.filename).resolve()
                if not destination.is_relative_to((MODELS / "translation").resolve()):
                    raise RuntimeError("Unsafe path in translation model archive.")
            package.extractall(MODELS / "translation")
    with zipfile.ZipFile(MODELS / "vosk-model-small-en-us-0.15.zip") as package:
        for item in package.infolist():
            if not (MODELS / item.filename).resolve().is_relative_to(MODELS.resolve()):
                raise RuntimeError("Unsafe ASR archive path.")
        package.extractall(MODELS)
    (MODELS / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(json.dumps(manifest, indent=2), flush=True)

if __name__ == "__main__":
    main()
