$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
dotnet run --project tests/GameDub.Tests -- --integration --process-audio --screen --neural --asr --duck --windows
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; inspect artifacts/tests for exact results.' }
