# Dependency and model licenses

GameDub's source is GPL-3.0-or-later. No service charges, expiring credits, accounts or provider credentials are used. This inventory concerns the local dependencies; it is not a promise of voice naturalness or story-translation accuracy.

| Component | License / use | Evidence |
|---|---|---|
| .NET / WPF runtime, SDK | MIT; preserve runtime notices | Bundled runtime `LICENSE.txt` / `ThirdPartyNotices.txt`; [dotnet](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |
| NAudio 2.2.1 and transitive NAudio packages | MIT; retain notice | `licenses/NAudio-MIT.txt`; [upstream](https://github.com/naudio/NAudio/blob/v2.2.1/license.txt) |
| CPython 3.12.10 embedded | PSF v2 and incorporated notices; redistribution permitted with notices | `runtime/LICENSE.txt`; [Python license](https://docs.python.org/3.12/license.html) |
| Kokoro-82M v1.0 weights / Hindi voice pack | Apache-2.0 model declaration; preserve model/license notices | `licenses/Kokoro-model-card.md`, `licenses/Kokoro-voices.md`; [model](https://huggingface.co/hexgrad/Kokoro-82M) |
| Kokoro ONNX conversion | Upstream Apache-2.0 model; conversion retained | [conversion](https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX); `models/manifest.json` |
| Local promoted FP32 model | Modified arithmetic/types of the FP16 export; does not recover discarded weight precision | `scripts/promote_kokoro.py`, original + generated hashes; Apache-2.0 source model |
| kokoro-onnx 0.6.1 | MIT | `licenses/kokoro-onnx-MIT.txt`; [upstream](https://github.com/thewh1teagle/kokoro-onnx) |
| Marian / Helsinki-NLP opus-mt-en-hi | Apache-2.0 declaration on this specific model card | `licenses/Marian-model-card.md`; [model](https://huggingface.co/Helsinki-NLP/opus-mt-en-hi) |
| Xenova Marian ONNX conversion | Conversion of the same upstream model; keep source attribution | [conversion model card](https://huggingface.co/Xenova/opus-mt-en-hi), `models/marian-en-hi/manifest.json` |
| Vosk API + small English model 0.15 | Apache-2.0; retain notices | `licenses/Vosk-Apache-2.0.txt`; [API](https://github.com/alphacep/vosk-api), [official model list](https://alphacephei.com/vosk/models) |
| eSpeak NG / phonemizer | GPL-3.0-family components; source/notice obligations apply when redistributing binaries. No subscription. | Bundled package license files; [eSpeak NG](https://github.com/espeak-ng/espeak-ng), [phonemizer](https://github.com/bootphon/phonemizer) |
| espeakng-loader wrapper | MIT wrapper; its bundled eSpeak NG binary retains GPL terms | [loader source/license](https://github.com/thewh1teagle/espeakng-loader) |
| ONNX Runtime | MIT; runtime uses CPUExecutionProvider only | [upstream](https://github.com/microsoft/onnxruntime); package notices |
| NumPy, SentencePiece, psutil, CFFI, requests and remaining Python dependencies | Permissive/BSD/Apache/MIT or documented dependency-specific terms; certifi includes MPL-2.0 certificate data | Complete version, declared-license and notice-file inventory in `licenses/python-dependencies.json` and `licenses/python/` |
| Argos translation baseline (not the selected app engine) | MIT/CC0, including model binaries per maintainer clarification | `licenses/Argos-MIT.txt`; [maintainer statement](https://github.com/argosopentech/argos-translate/issues/533) |

The package retains standard Apache-2.0 and GPL-3.0 texts. Preserve each component's copyright and license notices. If redistributing GPL components, include their complete corresponding source or a compliant source offer as applicable. This local build has not been published or uploaded. Generated speech is not automatically relicensed as GPL merely because a GPL program is used; third-party model and content rights remain separate.

## Candidates deliberately not installed

- **XTTS-v2:** Hindi is supported, but the Coqui Public Model License restricts commercial use of the model/output. It was excluded for a potential YouTube workflow. [Model and license](https://huggingface.co/coqui/XTTS-v2).
- **Piper Pratham/Priyamvada Hindi:** model cards identify CC BY-NC-SA 4.0 training material; repository-level metadata does not resolve model/output rights. **Rohan:** the model card references an IndicTTS license requiring separate review. These were not selected. [Pratham](https://huggingface.co/rhasspy/piper-voices/blob/main/hi/hi_IN/pratham/medium/MODEL_CARD), [Rohan](https://huggingface.co/rhasspy/piper-voices/blob/main/hi/hi_IN/rohan/medium/MODEL_CARD).
- **Chatterbox Multilingual:** MIT and Hindi-capable; larger model/dependency footprint and reference-voice requirements were not installed during this pass. It was researched, not benchmarked. [Official project](https://github.com/resemble-ai/chatterbox).
- **IndicTrans2 distilled 200M:** MIT model, but the official Hugging Face repository requires user agreement/login. No gate was bypassed and no account was created. [Official model](https://huggingface.co/ai4bharat/indictrans2-en-indic-dist-200M).

No paid-provider adapter is present in the final app. Earlier cloud candidates were removed after the free-only instruction; no credential-backed requests were made.
