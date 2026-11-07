"""Produce reports from actual test/benchmark artifacts, never generated performance estimates."""
from pathlib import Path
import json,statistics,math
from datetime import datetime,timezone,timedelta
ROOT=Path(__file__).resolve().parents[1]
load=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
result_path=sorted((ROOT/'artifacts'/'tests').glob('*/results.json'))[-1]
tests=load(result_path);passed=sum(t['passed'] for t in tests)
bench=load(ROOT/'artifacts'/'model-evaluation'/'voice-benchmark.json')['summary']
neural=load(result_path.parent/'neural-latency.json')
screen=load(result_path.parent/'screen-latency.json')
memory=load(result_path.parent/'worker-memory.json')
asr=load(result_path.parent/'asr-pipeline.json')
smoke=load(ROOT/'artifacts'/'app-smoke.json')
relative=str(result_path.relative_to(ROOT)).replace('\\','/')
screen_times=sorted(x['FirstAudioMs'] for x in screen)
now=datetime.now(timezone(timedelta(hours=5,minutes=30))).isoformat(timespec='seconds')
test_names='\n'.join(f"- {'PASS' if t['passed'] else 'FAIL'} — {t['name']} ({t['ms']:.0f} ms)" for t in tests)
(ROOT/'TEST_RESULTS.md').write_text(f'''# Actual test results

Report generated {now}. Final comprehensive suite: **{passed}/{len(tests)} passed**.

Command: `dotnet run --project tests/GameDub.Tests -- --integration --process-audio --screen --neural --asr --duck`

Machine-readable evidence: `{relative}`. These are executable assertion-based unit/integration tests, including real Windows OCR, process audio capture and WASAPI playback; no cloud mocks are represented as live services.

{test_names}

## Application and dependencies

The self-contained .NET 8 WPF application was published and separately launched with `--smoke-test`, using the bundled embedded Python runtime. It loaded both selected models and shut down with exit code **{smoke['exitCode']}**; ready state: **{smoke['ready']}**. Evidence: `artifacts/app-smoke.json`. Python dependency checks reported no broken requirements. The selected ONNX Runtime is CPU-only 1.24.4; DirectML is not installed in the final runtime.

Twenty neural speech files were synthesized successfully: ten dialogue scenarios × two voice configurations. Thirty-two raw translations were evaluated separately from the curated override dataset. No automatic naturalness, emotional-realism or human translation-accuracy score was assigned.

## Failures found and corrected

Earlier raw run folders remain available in `artifacts/tests`. Corrected issues included a silent/non-finite FP16 voice generation, fixture subtitles advancing before cold model startup, a bit-exact-zero capture test that rejected one-LSB resampling dither, an unsupported endpoint preventing ducking, and WPF reentrant shutdown during the app smoke test. Final tests use promoted FP32 arithmetic, warm models before fixtures, a noise-floor-aware isolation assertion, endpoint skipping, and deferred close.

## Not validated

No real game, protected/fullscreen capture, FPS impact, multi-hour soak, disconnected-device recovery, or combined real-game automatic hybrid session was tested. The ASR fixture recognized `{asr['recognized']}` from synthetic English; this contains errors and proves pipeline operation, not speech-recognition accuracy. Actual actor speech and noisy gameplay still require testing. UI automation was stopped with Escape; subsequent checks used programmatic fixtures and the application startup/shutdown smoke test. Final interactive visual QA is incomplete.
''',encoding='utf-8')
(ROOT/'PERFORMANCE_REPORT.md').write_text(f'''# Measured performance

Measured on Windows 11 with Intel i3-10105F, RTX 5060 Ti and 16 GB RAM. No game was running. Numbers are development-session observations, not gaming guarantees. Models use CPUExecutionProvider, two inference threads, BelowNormal worker priority and disabled idle spinning.

## Neural synthesis benchmark

`artifacts/model-evaluation/voice-benchmark.json`: **{bench['count']} actual generations**, ten Hindi lines × Alpha/Omega. Each measurement includes complete sentence/line generation, not acoustic arrival.

| Measurement | Observed |
|---|---:|
| Median synthesis | {bench['medianMs']:.0f} ms |
| p95 synthesis | {bench['p95Ms']:.0f} ms |
| Worst synthesis | {bench['worstMs']:.0f} ms |
| Peak benchmark-process RAM | {bench['peakRssMiB']:.1f} MiB |
| Mean process CPU, normalized across 8 logical CPUs | {bench['meanCpuPercentOneCore']/bench['logicalCpus']:.1f}% |
| Peak process CPU, normalized across 8 logical CPUs | {bench['peakCpuPercentOneCore']/bench['logicalCpus']:.1f}% |

psutil uses 100% to mean one logical CPU; normalization above is explicit. The benchmark process contains translation and voice models. The synthetic fixture, startup and benchmark have different allocation/lifecycle behavior.

## First playback request

| Path | Observation |
|---|---:|
| Warm unseen English → MT → sentence-pipelined Hindi, one line | {neural[0]['FirstAudioMs']:.1f} ms |
| Exact same line/context from generated-audio cache, one replay | {neural[1]['FirstAudioMs']:.1f} ms |
| Actual screen-crop → OCR → neural fixture, two recognized lines: median | {statistics.median(screen_times):.1f} ms |
| Same two-line fixture: p95 / worst | {screen_times[-1]:.1f} ms |
| Audio recognition fixture: recognized English → first Hindi playback request | {asr['record']['FirstAudioMs']:.1f} ms |

Pipeline timing begins after the dialogue event is submitted. OCR capture, stabilization (typically at least 220 ms), ASR endpointing, WASAPI buffering (requested 60 ms) and acoustic/hardware delay are not included in these playback-request values. ASR/capture end-to-end latency was not measured. Two lines and one cache replay are small samples, not reliable population estimates. `TotalMs` in completed-line records includes speech playback; snapshot records can contain zero when completion was not recorded.

After the neural integration run, observed separate workers used {memory['ttsMiB']:.1f} MiB (voice) and {memory['translationMiB']:.1f} MiB (translation); the test host used {memory['testHostMiB']:.1f} MiB. App startup workers used {smoke['voiceMiB']:.1f} / {smoke['translationMiB']:.1f} MiB. ASR adds another process when enabled.

## Limits and rejected backends

The original int8 Kokoro export took approximately 14–17 seconds for a short line and was rejected. The alternate FP16 export was faster but failed on one voice/line. The final local FP32 arithmetic promotion passed the 20-generation benchmark. DirectML failed on ConvTranspose and is not shipped. ONNX model inference does not use the GPU; total/per-process GPU usage, game frame times and FPS impact remain unmeasured. A system NVIDIA snapshot showed 0% utilization and 1568 MiB total VRAM use, which is not an app-attributed gaming measurement.

The 500–800 ms uncached subtitle target is not met. Cached playback was fast in this fixture. Multi-sentence synthesis/playback overlap improves first playback while keeping one waiting sentence and retaining cancellation. Longer or rapidly skipped dialogue can still be dropped.
''',encoding='utf-8')
(ROOT/'FINAL_DEVELOPMENT_REPORT.md').write_text(f'''# GameDub AI development handoff

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

Final comprehensive run: **{passed}/{len(tests)} tests passed**. The application startup/shutdown smoke exited **{smoke['exitCode']}** using the bundled Python runtime. Actual rendered subtitles and synthetic process audio both reached audible Hindi playback via WASAPI. This is harness integration, not real-game validation.

Final 20-generation benchmark: median **{bench['medianMs']/1000:.2f} s**, p95 **{bench['p95Ms']/1000:.2f} s**. The warm two-sentence pipeline requested first playback after **{neural[0]['FirstAudioMs']/1000:.2f} s**; its exact cache replay took **{neural[1]['FirstAudioMs']:.0f} ms**. Hardware/acoustic delay and gaming FPS are not measured. See the test/performance reports for scope and evidence.

## Incomplete and next priorities

1. Test a supported real game and a long session; measure FPS/frame times and failure behavior during skipping, pause, device changes and exit.
2. Improve localization: the small free MT model makes meaning, idiom and pronoun errors. Review key lines and glossary terms. IndicTrans2 was not downloaded through its access gate; larger models were not silently installed.
3. Improve voice performance: Kokoro has no reliable emotional acting control here. Human Hindi pronunciation/naturalness review is still needed; a larger expressive free model is a future benchmark, not an established winner.
4. Improve noisy-game ASR. The synthetic English fixture already contains recognition errors. Automatic hybrid arbitration passes contract tests, but its combined real-game behavior is unverified.
5. True streaming neural inference, diarization, acoustic emotion detection, source separation, lip sync, seamless output-device migration and an installer remain unimplemented.

No game files, OBS/recording presets, Windows security settings or personal files were modified. No gameplay recording, microphone capture, API credential use, account creation, billing activation, upload or publication occurred. Computer-use automation was stopped when Escape was pressed.
''',encoding='utf-8')
print('Wrote actual test, performance and handoff reports',passed,len(tests),flush=True)
