# Actual test results

**Window-selection fix:** a later targeted run passed **21/21 checks** (`artifacts/tests/20261107-182151/results.json`), including new native-window lifecycle regressions. The WPF target smoke exited 0 and selected the actual running GTA5_Enhanced process. See `WINDOW_SELECTION_FIX.md`. The 27-test comprehensive audio/AI run below predates this UI/window fix; it is retained as separate evidence rather than represented as a newly executed full suite.

Report generated 2026-11-07T17:13:05+05:30. Final comprehensive suite: **27/27 passed**.

Command: `dotnet run --project tests/GameDub.Tests -- --integration --process-audio --screen --neural --asr --duck`

Machine-readable evidence: `artifacts/tests/20261107-165151/results.json`. These are executable assertion-based unit/integration tests, including real Windows OCR, process audio capture and WASAPI playback; no cloud mocks are represented as live services.

- PASS — dataset contains >=30 contextual Hindi lines (40 ms)
- PASS — subtitle stabilization and duplicate suppression (10 ms)
- PASS — partial subtitles wait and clearing allows a later repeat (0 ms)
- PASS — speaker parsing and safe unknown identity (1 ms)
- PASS — context memory bounded and resettable (1 ms)
- PASS — context, voices, glossary and model version invalidate cache (15 ms)
- PASS — cache roundtrip, bounded eviction and corrupt-entry recovery (35 ms)
- PASS — unknown fixture never pretends to translate (3 ms)
- PASS — ambiguous fixture requires explicit context (3 ms)
- PASS — latest-line routing cancels old work and bounds backlog (662 ms)
- PASS — stop cancels translation and produces no playback (156 ms)
- PASS — stale generation budget drops delayed speech (47 ms)
- PASS — provider failure is reported and next dialogue recovers (173 ms)
- PASS — automatic mode prefers subtitles and suppresses cross-source duplicate speech (4 ms)
- PASS — pause and loading labels are not spoken (0 ms)
- PASS — sentence playback begins before later synthesis and cancellation discards partial cache (43 ms)
- PASS — sentence order and complete generated-audio cache are preserved (54 ms)
- PASS — real Windows OCR recognizes a rendered English subtitle (197 ms)
- PASS — real offline model translates an unseen line (3457 ms)
- PASS — real offline glossary keeps a configured character name (3096 ms)
- PASS — real Hindi WAV synthesis and actual WASAPI device playback (2652 ms)
- PASS — real playback cancellation responds without waiting for clip end (255 ms)
- PASS — real per-process loopback captures only a synthetic child (8662 ms)
- PASS — actual window crop → OCR → MT → Hindi TTS → WASAPI (30120 ms)
- PASS — real neural Hindi synthesis, playback and context-safe cache replay (15450 ms)
- PASS — per-process synthetic English → local ASR → Hindi neural playback (29906 ms)
- PASS — selected-process ducking restores original session volume after cancellation (4268 ms)

## Application and dependencies

The self-contained .NET 8 WPF application was published and separately launched with `--smoke-test`, using the bundled embedded Python runtime. It loaded both selected models and shut down with exit code **0**; ready state: **True**. Evidence: `artifacts/app-smoke.json`. Python dependency checks reported no broken requirements. The selected ONNX Runtime is CPU-only 1.24.4; DirectML is not installed in the final runtime.

Twenty neural speech files were synthesized successfully: ten dialogue scenarios × two voice configurations. Thirty-two raw translations were evaluated separately from the curated override dataset. No automatic naturalness, emotional-realism or human translation-accuracy score was assigned.

## Failures found and corrected

Earlier raw run folders remain available in `artifacts/tests`. Corrected issues included a silent/non-finite FP16 voice generation, fixture subtitles advancing before cold model startup, a bit-exact-zero capture test that rejected one-LSB resampling dither, an unsupported endpoint preventing ducking, and WPF reentrant shutdown during the app smoke test. Final tests use promoted FP32 arithmetic, warm models before fixtures, a noise-floor-aware isolation assertion, endpoint skipping, and deferred close.

## Not validated

No real game, protected/fullscreen capture, FPS impact, multi-hour soak, disconnected-device recovery, or combined real-game automatic hybrid session was tested. The ASR fixture recognized `the region bro can find another way we need to get out of here man region can find another way` from synthetic English; this contains errors and proves pipeline operation, not speech-recognition accuracy. Actual actor speech and noisy gameplay still require testing. UI automation was stopped with Escape; subsequent checks used programmatic fixtures and the application startup/shutdown smoke test. Final interactive visual QA is incomplete.
# Live silence follow-up — 2026-11-07, 20:16

`dotnet run --project tests/GameDub.Tests -- --integration`: **29/29 passed** (`artifacts/tests/20261107-201604/results.json`). Includes real OCR, unseen offline translation, glossary, WASAPI playback/cancellation, warm model survival after cancellation and correct reply alignment. OCR punctuation/spacing noise no longer restarts a confirmed line. Real neural Voice lab playback in the corrected running app was requested at **8745 ms** under the current GTA load (Quality mode, fresh generated sample); user audibility confirmation is pending. No gameplay latency claim is made.

The earlier short-screen integration retries (`200603`, `200729`, `200928`) failed/time out and remain recorded. They are not included in the 29 passing checks. The current session reached actual GTA OCR/translation before the warm-worker fix, but never reached playback in that old live log. After this fix GTA was on Mission Failed; actual post-fix story dialogue playback has not yet been observed. Selected GTA process PID 22420, region 340,925 / 1240×100, Quality mode saved. Dubbing was started again.

