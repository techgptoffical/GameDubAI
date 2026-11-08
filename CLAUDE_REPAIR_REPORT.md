# GameDub AI: repair report (2026-11-08)

Build: `build\GameDubAI-claude\GameDub.App.exe` (`Run GameDub.cmd` now prefers it). Older builds are untouched.
Branch `repair-performance`. Commit 920a5c2 checkpoints Codex's unfinished work as-is. Backups of your profile and settings are in `local-settings\repair-backups\*-before-claude-repair.json`.

## 1. Long subtitles (OCR)
**Cause:** your saved GTA traces show the crop strip was 72–92 px (y=987–1079 / 1007–1079). GTA draws subtitles centred and bottom-anchored, and they grow upward about 45 px per row at 1080p. Row 1 of two-row captions was cut, so "all your mental health needs." reached the translator alone. The strip also included the minimap and button legend.

**Fix:**
- OCR now returns line boxes. Rows centred on the game window are kept and grouped bottom-up into one caption, joined in reading order.
- HUD, minimap and menu-legend rows are rejected by position and by legend vocabulary.
- A pixel check flags glyphs cut by the crop's top or side edge. The monitor then grows the region and saves it automatically.
- GTA profiles migrate once to a centred crop covering 15% of the height (317,917 / 1286×162 at 1080p). It scales with resolution.
- OCR noise ("D ve Norton") no longer causes duplicates. A lower row that starts lower-case waits briefly for its upper row. If the full caption arrives late, it replaces the partial one when the partial hasn't been spoken yet.
- GTA uses two OCR passes (raw plus outline mask), keeping the cleaner reading of each row. These run only on changed frames: p50 75 ms.
- Diagnostics now save `seen-*.png` overlays next to each crop: green = sent to translation, red = rejected, orange bar = clipped.

**Tests (`--ocr`):** 29/30 rendered captions read with ≥90% word recall: 1, 2 and 3 rows, bright/dark/busy scenes, HUD and legend present, with 0 HUD leaks. The one miss read "wavout" for "way out" on the busy background. Other tests:
- the old 72 px strip is detected and grown until both rows are read
- fades and rapid changes emit each caption once
- menu rows are rejected
- **the real clipped GTA crop is detected and a real single-row crop is not**

## 2. Formal / wrong Hindi
**Cause:** the sentence-level MT models themselves (Marian, NLLB). They are trained on formal text and see no context. OCR, segmentation and configuration were not the main problem. On 60 new game lines (`research/translation-benchmark/lines.json`, never used by the app), Marian got many meanings wrong:
- "I'm done" → "मैं कर रहा हूँ"
- "you trippin'" → "यात्रा के लिए"
- "Yo dog" → "कुत्ता"

Word replacement cannot fix that. It also broke grammar where a swap changed grammatical gender (प्रतीक्षा→इंतज़ार).

**Fix:** Gemma 4 E4B (Apache-2.0) runs in one persistent llama.cpp Vulkan server, loopback only. It gets scene history, speaker, glossary, and speaker gender, but only from your character→voice mapping. It writes a short plain-English paraphrase first, then Hindi.

A meaning guard rejects lines that:
- lose a negation, a number or a glossary term
- leak Latin text
- mix तू/तुम/आप

A rejected line falls back to Marian. Codex's word list is now only a gender-safe cleanup on that fallback path.

**Result (`--hindi-benchmark`; reviewed by me, not a native-speaker panel):**
- 0/60 fallbacks; p50 888 ms, p95 1.4 s per line
- Most lines are natural spoken Hindi:
  - "Yo dog, quit playing…" → "अरे यार, अब नाटक बंद कर और मुझे लेने आ"
  - "Drop the gun…" → "हथियार नीचे फेंक और हाथ दिखा"
- Remaining faults I found: about 4 meaning slips and 2 gender/register slips. Examples:
  - u23 garbled "बेफ्रिक किया"
  - u25 "lose the cops" became "run faster than the cops"
  - Amanda with masculine "चाहता" when no voice is mapped
- Outputs: `artifacts/translation-benchmark/` (`baseline-mt.json`, `conversational-v6.json`).

## 3. Delay
Live Cinematic is now the default:
- the line that is playing is never interrupted
- at most 2 lines wait; older waiting lines become text in History
- lines older than 6 s are not spoken
- fresh lines are translated first, and stale ones are still translated for History

Story Complete asks for Catch up after 20 s of lag (was 8 s).

Real chain (`--live-speech`): 50 rendered unseen captions → OCR → Gemma → Kokoro DirectML → WASAPI.

| | Live Cinematic | Story Complete |
|---|---|---|
| captured / spoken | 50 / 42 (8 text-only) | 50 / 50 |
| translation p50 / p95 | 875 / 2385 ms | 797 / 876 ms |
| TTS first chunk p50 | 1072 ms | 999 ms |
| queue wait p50 | 686 ms | 2887 ms |
| caption → audio p50 / p95 | **3.2 / 4.1 s** | **5.2 / 7.3 s** (Codex run: 44.7 s median) |

The bottleneck is Hindi speech length (p50 2.2 s) plus about 1 s of Kokoro first chunk and about 0.9 s of translation. Each new line costs roughly 2 s of preparation, and that overlaps the previous line's playback.

## 4. Voices (not solved)
Quality mode uses Kokoro-82M Hindi (4 voices, espeak-ng phonemes) at speed 1.0. Its accent and flat prosody come from the model itself, not from the settings. Better phrasing helps a little: `artifacts/voice-comparison/*.wav` has the same voice reading Marian text and the new text.

The two realistic upgrades are IndicF5 (MIT, voice-prompted) and Indic Parler-TTS (Apache-2.0, named Hindi speakers, emotion prompts). Both are **gated on Hugging Face** (your account must accept their terms) and need about 3 GB of PyTorch CUDA. I did not download them.

GTA V subtitles carry no speaker names. Character voices therefore come from labels, manual selection, or the consistent default.

## 5. Resources and limits
- The LLM server uses about 3–4 GB VRAM and about 3 GB RAM (the per-layer embeddings stay on the CPU), and takes about 20 s to start. Model processes now run in a kill-on-close job, so they die with the app even after a crash.
- **No real GTA gameplay test was run.** GTA was not running. The only real-game evidence is the replayed crops from your earlier sessions. FPS impact with the LLM sharing the GPU is unmeasured.
- Tests: 53/53 default, 6/6 `--ocr`, 2/2 `--live-speech`.
