# GameDub AI — user guide

English game subtitles → offline Hindi translation → local neural Hindi speech. Also includes experimental process-audio recognition and automatic fallback. No accounts, API keys, subscriptions, microphones, game injection or game-file changes.

## Run now

Double-click **Run GameDub.cmd** in this folder. The launcher prefers the updated `build/GameDubAI-next/GameDub.App.exe` when present, otherwise `build/GameDubAI/GameDub.App.exe`. Close older GameDub windows before relaunching. Keep the `workers`, `models`, and Python runtime folders beside the project. This build targets Windows 11 x64.

1. Open **Voice lab** and press **Translate + play A**. Try **Compare A / B** for Alpha and Omega.
2. For an automatic demonstration, open **Dubbing → Test demo (not your game)**. Its region is selected automatically. Press **Start dubbing**, then **Play scene** in the demo. Keep the demo in front. The demo is a separate synthetic source; it does not dub the game behind it.
3. For a game, enable English subtitles and use borderless/windowed mode. The list prioritizes known game processes and selects a sole detected game. Confirm the process shown under the list (for GTA V Enhanced: `GTA5_Enhanced`, not `steamwebhelper`). Select the game and drag a small region around its subtitles. If missing, click **Pick game in 5 seconds**, then switch to the game during the countdown. Closing the test demo clears its selection/region; windows are refreshed when returning to GameDub.
4. Press **Start dubbing** and return to the game. **Stop / cancel speech** cancels generation and audio. Alt-tabbing suspends recognition/playback.

Windows English OCR must be installed. Do not select OBS or recording software as the source. Captures are transient subtitle crops in memory, never screen recordings.

## Audio setup

Choose the output device and Hindi volume in Settings, then save. For the cleanest mix, manually lower the game's independent dialogue/voice slider while leaving music/effects on, and verify subtitles still appear. GameDub does not change game settings.

Optional ducking reduces only matching game audio sessions to 30% during Hindi playback and restores their volume on completion/cancellation. It lowers the **entire game mix**, including music/effects; it does not isolate speech. Other applications and master volume are not changed. Disable it if the game's audio session cannot be controlled.

## Dialogue sources

- **Subtitles:** preferred; foreground selected-window crop, native English OCR, stabilization and duplicate filtering.
- **Game audio:** experimental Windows per-process loopback → Vosk English recognition → translation/TTS. Captures the game PID tree, not the microphone or GameDub output. Mixed music/effects can cause recognition errors.
- **Automatic:** keeps the subtitle path preferred, uses audio when reliable subtitles are absent, and suppresses duplicate speech between paths. Its source arbitration is tested; a combined real-game session has not been tested.

Audio modes require an external process. The built-in subtitle demo belongs to GameDub, so audio capture excludes it to prevent feedback.

## Voices and quality

Kokoro supplies four Hindi voices: `hf_alpha`, `hf_beta`, `hm_omega`, `hm_psi`. Settings/profile voice IDs are persistent. There is no actor cloning. No diarization is implemented; unknown speakers use the default voice.

The bundled neural runtime does not require a system Python installation. The separate eSpeak test fallback and synthetic-English test source require an installed eSpeak NG console executable; it was already installed on the development PC.

**This is not yet professional cinematic localization.** Arbitrary dialogue uses a small Marian/OPUS-MT model. It can mistranslate slang, pronouns, sarcasm and story meaning, and does not reason over scene history. Context/relationships are retained for profiles/cache identity and future providers. The bundled 32 examples use curated reference overrides; these are labeled separately from actual model translations. They are not evidence of general translation accuracy.

Kokoro is a neural voice improvement over the robotic eSpeak test fallback, but has no supported Hindi emotion/whisper/shout controls here. Delivery hints do not provide acoustic emotion detection. Use the human rating tool and listening samples to judge it yourself.

Balanced uses a 6-second generation budget; Quality permits at least 10 seconds; Performance caps it at 4 seconds and uses a small 1.05× synthesis speed change. Late lines are dropped, never queued indefinitely. These modes do not imply different emotional acting models. Multi-sentence Hindi starts after its first sentence is generated, with the following sentence produced during playback and a one-chunk waiting buffer. Each sentence still requires full neural synthesis; this is not a token-by-token streaming decoder. An interrupted partial line is not cached as a complete utterance.

## Profiles, glossary and review

Select a game, then edit its JSON in Settings. `Voices` maps exact subtitle speaker labels to voice IDs. `Glossary` fixes named terms when their translation can be verified; uncertain matches are dropped rather than silently losing a term. `ReviewedLines` can override either an exact English line or `Speaker: English line`. These overrides are useful for important dialogue and ambiguous phrasing. Confirm your own Hindi wording.

`profiles/example-story-game.json` is a template. Persistent data stays under `local-settings` in this project. The generated-audio cache is bounded to 128 MiB and includes game, context, voice, model/version and glossary identity. No raw game voice recordings are stored. Human ratings and explicitly exported diagnostics are local files.

## Build and test

The current folder is already set up. To recreate it: install Python 3.12 x64 and a .NET 8+ SDK using their normal supported installers, then run `./Setup.ps1`. The script downloads public free models only, checks disk space, builds local FP32 arithmetic from the upstream Kokoro export, and publishes a self-contained .NET application. It does not bypass PowerShell policy or Windows prompts. If scripting is restricted, run its listed commands manually in a permitted terminal.

Run `./Test.ps1` for the comprehensive suite. It opens a synthetic subtitle fixture and plays short synthetic English/Hindi samples. It does not record the screen, use a microphone, modify OBS or require a game. Machine-readable results go to `artifacts/tests/<timestamp>/results.json`.

`scripts/benchmark_local.py` generates ten Hindi lines with two voice configurations and records actual synthesis/CPU/RAM measurements. `scripts/evaluate_marian.py` and `scripts/evaluate_argos.py` save actual unmodified translations independently of reference overrides. See [TEST_RESULTS.md](TEST_RESULTS.md), [PERFORMANCE_REPORT.md](PERFORMANCE_REPORT.md), [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md) and [FINAL_DEVELOPMENT_REPORT.md](FINAL_DEVELOPMENT_REPORT.md).

## Known limits

No real game or FPS-impact test was completed. Fullscreen/protected surfaces, overlapping subtitles, OCR errors and unreliable speaker labels can fail. Cutscene interruption follows subtitle disappearance/changes (including a 700 ms clear debounce), not the game's internal cutscene state. Output-device changes require selecting a device and restarting; disconnect errors stop the current line. Ducking does not cover new sessions created midway through a line. Abruptly killing the process can prevent volume restoration; ordinary Stop/close paths restore it. No lip synchronization, source separation, advanced emotion analysis, voice cloning or installer is included.

The generated WAV examples in `artifacts/model-evaluation` are synthetic listening material. Current source is provided under GPL-3.0-or-later; third-party models/libraries retain their own licenses. See the notices before redistributing the software.
