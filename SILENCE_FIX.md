# Short-subtitle silence fix — 2026-11-07

The user's GTA session log showed subtitles detected and translation started, followed by cancellation when the subtitle disappeared. There were no playback measurements in that session. The UI was stopped when inspected.

Previously, a blank subtitle region for 700 ms called the same hard interruption used by Stop or changing games. That could suppress every short line while local translation/synthesis was still running. Blank regions now release subtitle-source arbitration without cancelling the last confirmed line. Generation is still bounded by the selected mode's deadline. A new confirmed line, Stop, switching away, a closed game, or an invalid capture region still cancels obsolete work immediately. Automatic mode retains its one-second audio fallback holdoff.

Live setup: GTA5_Enhanced selected; game subtitles inspected and already On. Default Windows audio output, volume 70%, offline Marian translation, Kokoro Hindi neural speech, no game-audio ducking. The old 413,990 / 1206×89 region was replaced with 290,895 / 1340×145 for this 1920×1080 game window, covering a wider bottom-center subtitle band. Re-select it if resolution or HUD positioning changes.

The running app's Voice lab translated the bundled sample to “हमें यहाँ से निकलना होगा!” and requested WASAPI playback at 179 ms from cache. This confirms a playback request, not that the user heard it through their headphones.

Regression run `artifacts/tests/20261107-200451/results.json`: 23/23 checks passed, including subtitle-gap completion, replacement cancellation and automatic source holdoff. The first real-screen integration attempt (`20261107-200603`) timed out because Windows did not foreground the synthetic source; the separate real neural speech/cache test passed. The fixture now waits for foreground before advancing its first short subtitle and allows time for explicit test-window activation. Subsequent results are recorded in TEST_RESULTS.md.

No game files, OBS settings, API keys, microphone input or cloud services were used for this fix. No game screenshots or raw game audio were saved.

## Follow-up: repeated cancellation kept models cold

The next live GTA log showed repeated OCR corrections and foreground changes cancelling translation/synthesis, with no playback-start measurement. `LocalWorker` previously killed Python on every cancellation. That forced model reloads on the next line, so a busy dialogue scene could keep restarting indefinitely. Cancelled calls now return immediately while a single background exchange drains and discards its result. The worker stays warm; its semaphore serializes all requests, cancelled queued work is never sent, and no discarded result reaches playback. A 60-second worker watchdog and app disposal can still terminate a failed/closing worker.

Subtitle comparison now ignores spacing and punctuation noise. It continues to distinguish letter/word changes (including negation). The region was refined to 340,925 / 1240×100 to avoid the bottom-right location HUD; Quality mode is persisted. This positioning still needs checking against the user's current subtitle/HUD layout.

`artifacts/tests/20261107-201604/results.json`: 29/29 passed, including real warm-worker cancellation, protocol reply alignment, actual unseen offline translation, OCR and WASAPI output. The cancellation test asserts return in under 500 ms and reuse of the same model process. The initial artifact's `cancellationMs` accidentally included waiting for the next inference; its check still measured the correct interval. The test now records those two intervals separately.

Earlier short-screen attempts (`200603`, `200729`, `200928`) did not pass; foreground handoff and generation budgets interfered. These failures remain recorded and are not presented as successful gameplay integration.

The updated app was republished as win-x64 self-contained. A stale framework-dependent runtime configuration left by the staging copy was replaced with the matching self-contained output; the real app reopened and reported local models ready. No runtime installer was downloaded or executed.
