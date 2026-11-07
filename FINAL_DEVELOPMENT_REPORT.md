# GameDub AI development handoff

**Window-selection update:** closed demo selections now clear correctly, GTA5_Enhanced is detected and selected as a real process, and a five-second picker supports unlisted windows. Launch the updated build through `Run GameDub.cmd`. See `WINDOW_SELECTION_FIX.md` for the targeted 21-check regression result and live GTA binding evidence.

Built a genuinely audible, free local Windows prototype. It is ready to open and test; the professional cinematic-game-dubbing objective is **not yet achieved**.

## Run

Double-click `Run GameDub.cmd`, or launch `build/GameDubAI/GameDub.App.exe`. Start with **Voice lab → Translate + play A**. Then use **Dubbing → Open subtitle demo → Start dubbing**, and **Play scene** in the foreground demo window. A real game needs English subtitles, a selected window and a dragged subtitle region. See `README.md` for setup and audio instructions.

The self-contained .NET build, embedded Python, model files and source are in this workspace. `artifacts/GameDubAI-Local-Windows.zip` is the local bundle created by `scripts/package_local.py`; it is not uploaded or published. On the development PC, no account, paid API or model setup is needed to run the neural path. A different PC still needs Windows English OCR and compatible native runtime support.

## Implemented

- Native WPF app, voice lab, four Hindi voice choices, A/B identity comparison, manual delivery hints, output selection/volume, diagnostics and human ratings.
- Foreground game-window detection, client-relative drag region, native OCR, stabilization, duplicate suppression, pause-label filtering and subtitle-clear interruption.
- Offline Marian translation, curated reference/profile overrides, strict glossary verification, persistent speaker mappings and six-line history. History is retained but the small MT model has no story reasoning.
- Local Kokoro Hindi synthesis, sentence-by-sentence generation/playback overlap, bounded queues, stale-line cancellation and version/context/voice-aware 128 MiB generated-audio cache.
- Process-specific Windows audio capture, offline Vosk recognition, experimental automatic source arbitration, and optional selected-session ducking with restoration.
- Bundled runtime, dependency locks/setup, public model provenance/hashes, license inventory, reusable tests and real performance artifacts. Workers reject Python socket connections; stored API credentials are not used.

Main changed areas: `src/GameDub.App`, `src/GameDub.Core`, `workers`, `tests/GameDub.Tests`, `tools/GameDub.TestSource`, `scripts`, `data`, `profiles`, `models`, `licenses`, and the root startup/setup/report files.

## Validation

Final comprehensive run: **27/27 tests passed**. The application startup/shutdown smoke exited **0** using the bundled Python runtime. Actual rendered subtitles and synthetic process audio both reached audible Hindi playback via WASAPI. This is harness integration, not real-game validation.

Final 20-generation benchmark: median **2.15 s**, p95 **3.24 s**. The warm two-sentence pipeline requested first playback after **2.40 s**; its exact cache replay took **28 ms**. Hardware/acoustic delay and gaming FPS are not measured. See the test/performance reports for scope and evidence.

## Incomplete and next priorities

1. Test a supported real game and a long session; measure FPS/frame times and failure behavior during skipping, pause, device changes and exit.
2. Improve localization: the small free MT model makes meaning, idiom and pronoun errors. Review key lines and glossary terms. IndicTrans2 was not downloaded through its access gate; larger models were not silently installed.
3. Improve voice performance: Kokoro has no reliable emotional acting control here. Human Hindi pronunciation/naturalness review is still needed; a larger expressive free model is a future benchmark, not an established winner.
4. Improve noisy-game ASR. The synthetic English fixture already contains recognition errors. Automatic hybrid arbitration passes contract tests, but its combined real-game behavior is unverified.
5. True streaming neural inference, diarization, acoustic emotion detection, source separation, lip sync, seamless output-device migration and an installer remain unimplemented.

No game files, OBS/recording presets, Windows security settings or personal files were modified. No gameplay recording, microphone capture, API credential use, account creation, billing activation, upload or publication occurred. Computer-use automation was stopped when Escape was pressed.
# Live setup follow-up — 2026-11-07

Fixed cancellation-induced model reloads: obsolete requests now return promptly and their replies are drained/discarded, retaining the warm local process. Added spacing/punctuation-resistant subtitle identity. Short subtitle gaps may finish pending Hindi within the existing deadline; real replacement dialogue and hard interruption still cancel it. See SILENCE_FIX.md and the separate test scope in TEST_RESULTS.md.

The standalone updated app was reopened successfully, local models reported ready, and a fresh neural Hindi sample reached WASAPI playback at 8.745 seconds under the current game load. Quality mode and the refined GTA subtitle region were saved, and live dubbing was enabled. The user has not confirmed hearing the test. GTA was at Mission Failed during the final setup; successful post-fix spoken story dialogue is still unverified.

