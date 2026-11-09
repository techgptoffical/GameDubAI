# Contributing

Thanks for helping improve GameDub AI.

## Setup

1. Windows 11 x64, the .NET 8 SDK, Python 3.12.
2. Run `./Setup.ps1` to create the Python runtime, fetch the models and build the app.
3. Open `GameDubAI.sln` in Visual Studio 2022 or Rider, or use `dotnet build`.

## Before you open a pull request

- Run `dotnet build GameDubAI.sln -c Release` with no new warnings.
- Run `./Test.ps1` if you touched the pipeline (`src/GameDub.Core`, `workers/`).
- Keep UI colours in `src/GameDub.App/Themes/Dark.xaml`; don't hard-code new ones in XAML.
- Don't commit models, runtimes, `build/`, `local-settings/` or API keys. `.gitignore` already covers them.
- Describe what you measured. Don't claim quality or speed you didn't test.

## Commit style

Use short imperative subjects, for example `Merge GTA sentences split across subtitles`.

## Reporting bugs

Open an issue with the game, your settings (Translation / Voice synthesis / Performance mode), what you expected, and `local-settings/last-error.txt` if it exists. Remove any API keys first.
