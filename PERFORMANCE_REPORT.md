# Measured performance

Measured on Windows 11 with Intel i3-10105F, RTX 5060 Ti and 16 GB RAM. No game was running. Numbers are development-session observations, not gaming guarantees. Models use CPUExecutionProvider, two inference threads, BelowNormal worker priority and disabled idle spinning.

## Neural synthesis benchmark

`artifacts/model-evaluation/voice-benchmark.json`: **20 actual generations**, ten Hindi lines × Alpha/Omega. Each measurement includes complete sentence/line generation, not acoustic arrival.

| Measurement | Observed |
|---|---:|
| Median synthesis | 2153 ms |
| p95 synthesis | 3235 ms |
| Worst synthesis | 3310 ms |
| Peak benchmark-process RAM | 687.0 MiB |
| Mean process CPU, normalized across 8 logical CPUs | 18.6% |
| Peak process CPU, normalized across 8 logical CPUs | 25.0% |

psutil uses 100% to mean one logical CPU; normalization above is explicit. The benchmark process contains translation and voice models. The synthetic fixture, startup and benchmark have different allocation/lifecycle behavior.

## First playback request

| Path | Observation |
|---|---:|
| Warm unseen English → MT → sentence-pipelined Hindi, one line | 2403.5 ms |
| Exact same line/context from generated-audio cache, one replay | 28.3 ms |
| Actual screen-crop → OCR → neural fixture, two recognized lines: median | 2373.0 ms |
| Same two-line fixture: p95 / worst | 2561.5 ms |
| Audio recognition fixture: recognized English → first Hindi playback request | 4956.5 ms |

Pipeline timing begins after the dialogue event is submitted. OCR capture, stabilization (typically at least 220 ms), ASR endpointing, WASAPI buffering (requested 60 ms) and acoustic/hardware delay are not included in these playback-request values. ASR/capture end-to-end latency was not measured. Two lines and one cache replay are small samples, not reliable population estimates. `TotalMs` in completed-line records includes speech playback; snapshot records can contain zero when completion was not recorded.

After the neural integration run, observed separate workers used 399.4 MiB (voice) and 267.9 MiB (translation); the test host used 87.2 MiB. App startup workers used 393.0 / 267.1 MiB. ASR adds another process when enabled.

## Limits and rejected backends

The original int8 Kokoro export took approximately 14–17 seconds for a short line and was rejected. The alternate FP16 export was faster but failed on one voice/line. The final local FP32 arithmetic promotion passed the 20-generation benchmark. DirectML failed on ConvTranspose and is not shipped. ONNX model inference does not use the GPU; total/per-process GPU usage, game frame times and FPS impact remain unmeasured. A system NVIDIA snapshot showed 0% utilization and 1568 MiB total VRAM use, which is not an app-attributed gaming measurement.

The 500–800 ms uncached subtitle target is not met. Cached playback was fast in this fixture. Multi-sentence synthesis/playback overlap improves first playback while keeping one waiting sentence and retaining cancellation. Longer or rapidly skipped dialogue can still be dropped.
