param([switch]$RunTests)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
function Check-Exit { if ($LASTEXITCODE -ne 0) { throw "Setup step failed with exit code $LASTEXITCODE" } }
if ((Get-PSDrive -Name ([IO.Path]::GetPathRoot($PSScriptRoot).TrimEnd('\').TrimEnd(':'))).Free -lt 3GB) { throw 'Keep at least 3 GB free for setup.' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'A .NET 8+ SDK is needed to build from source. The bundled application can run without an SDK.' }
if (-not (Test-Path -LiteralPath '.venv/Scripts/python.exe')) { python -m venv .venv; Check-Exit }
$python = Join-Path $PSScriptRoot '.venv/Scripts/python.exe'
& $python -m pip install --disable-pip-version-check -r requirements.lock.txt
Check-Exit
& $python scripts/setup_models.py
Check-Exit
& $python scripts/fetch_marian.py
Check-Exit
& $python scripts/promote_kokoro.py
Check-Exit
$buildOutput = 'build/GameDubAI-v1'
dotnet publish src/GameDub.App/GameDub.App.csproj -c Release -r win-x64 --self-contained true -o $buildOutput --nologo
Check-Exit
if ($RunTests) { dotnet run --project tests/GameDub.Tests -- --integration --process-audio --screen --neural --asr --duck --windows; Check-Exit }
Write-Host 'Ready. Double-click Run GameDub.cmd.'
