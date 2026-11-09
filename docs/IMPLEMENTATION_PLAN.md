# GameDub AI implementation order

1. **Implemented and exercised:** .NET 8 WPF voice lab; offline Marian translation with curated line overrides; Kokoro neural Hindi; eSpeak engineering fallback; cancellable selected-device WASAPI playback.
2. **Implemented and exercised:** running-window detection, client-relative region selector, in-memory native OCR, stabilization/deduplication, bounded latest-dialogue routing, interruption, screen-crop-to-neural-audio synthetic fixture.
3. **Implemented:** persistent profiles, manual voice identity mappings, strict glossary verification, six-line history, bounded context/version-aware audio cache, latency display/export, 32-line reference set and human-rating UI. Actual model outputs are evaluated separately from overrides.
4. **Implemented and exercised separately:** Windows process-loopback capture, isolation test, offline Vosk recognition to Hindi neural playback, optional selected-process ducking/restoration. Automatic source arbitration is tested; combined real-game hybrid behavior remains unverified.
5. **Runnable delivery:** self-contained .NET development/release build, project-local embedded Python, local models, startup/setup/test scripts and license inventory.
6. **Next priority:** a real supported-game session, gameplay FPS/frame-time measurements, long-session/device-switch testing, translation quality and human voice review. Current small MT and Kokoro are below the cinematic target. Larger expressive models, scene reasoning, acoustic emotion analysis, diarization, streaming neural decoding and an installer remain future work.

No microphone, game injection, game/OBS settings changes, screen recording or raw game-audio storage. Capture is a transient subtitle crop or an explicitly selected process stream. The game mix is unchanged unless optional, reversible session ducking is enabled. No paid services or stored API credentials are used. No cinematic-quality or game-FPS claim is made.
