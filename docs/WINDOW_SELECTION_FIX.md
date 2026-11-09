# Closed demo / running game selection fix

The reported screenshot selected `GameDub subtitle demo` even though GTA V was running behind it. The demo window had closed, so the capture loop correctly stopped that source, but the app incorrectly kept its selection and region visible. This was a selection/lifecycle bug, not evidence that GTA was unavailable to Windows.

## Changes

- Enumerate visible top-level windows, including secondary windows of a process. Check HWND ownership against the selected PID and resolve replacement windows only within that process.
- Prefer actual known game executable names, never a Steam/launcher window merely sharing the game's title. A single detected game can be selected automatically; ambiguous games require selection.
- Remove a closed demo and its region immediately. Refresh missing/dead targets and refresh when returning to GameDub. Keep explicit selections of unlisted games.
- Show the actual process/PID below the list, display a persistent demo warning, and add a five-second foreground-window picker.
- Fix dark-on-dark labels in the tab contents.

## Verification

`dotnet run --project tests/GameDub.Tests -- --windows` passed **21/21 checks**, including native multiple-window enumeration, HWND recreation, stale demo → GTA selection, rejecting a Steam game-title false positive, and retaining manual unlisted-game selections.

The WPF application's target-selection smoke test exited **0** and selected the live `GTA5_Enhanced` process, title `Grand Theft Auto V`, with its own `GTA5_Enhanced` profile and **no borrowed demo subtitle region**. Evidence: `artifacts/window-fix-smoke.json`, `artifacts/target-selection-smoke.json`, and `artifacts/game-window-diagnostics.json`.

No game settings, OBS settings, game files or recording controls were changed. This verifies the live game-window binding, not spoken GTA dialogue or FPS impact. A paused map has no dialogue to translate.

## Use the updated build

Close the older GameDub windows and launch **Run GameDub.cmd**, which now prefers `build/GameDubAI-next/GameDub.App.exe`. The original running build was left undisturbed so it can finish/stop safely; an old pinned executable shortcut still points to that older build.

Confirm `Grand Theft Auto V (GTA5_Enhanced)` in the selection, enable English subtitles yourself, select the game's subtitle region, press Start, and return to the game. For a missing game, use **Pick game in 5 seconds** and switch to it during the countdown. The standalone ZIP maps the updated build to its normal `build/GameDubAI` location.
