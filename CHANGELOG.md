# Changelog

## 1.0.0 (2026-11-09)

### Added
- New dark theme (`src/GameDub.App/Themes/Dark.xaml`): rounded buttons with a violet accent, dark drop-downs, underline tabs, card group boxes, slim scrollbars, styled checkboxes, slider and data grids.
- App icon and header with logo and mode badge.
- Dark window title bar on Windows 10/11.
- GitHub Actions build that publishes a self-contained `win-x64` zip and attaches it to tagged releases.
- README, CONTRIBUTING, CHANGELOG, `.editorconfig`, `.gitattributes`.

### Changed
- Development reports moved to `docs/`; the previous README is now `docs/USER_GUIDE.md`.
- `Setup.ps1` and `Run GameDub.cmd` use the `build/GameDubAI-v1` build.

### Earlier work
See `git log` and `docs/FINAL_DEVELOPMENT_REPORT.md`: live speech with GPU Kokoro, Gemini as an optional translator, sentence merging for GTA subtitles, the game-safe memory guard, window selection and silence fixes.
