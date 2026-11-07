$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$started = [DateTimeOffset]::Now
$executable = Join-Path $PSScriptRoot 'build/GameDubAI/GameDub.App.exe'
if (Test-Path -LiteralPath 'build/GameDubAI-next/GameDub.App.exe') { $executable = Join-Path $PSScriptRoot 'build/GameDubAI-next/GameDub.App.exe' }
$smoke = Start-Process -FilePath $executable -ArgumentList '--smoke-test' -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru
if (-not $smoke.WaitForExit(45000)) { Stop-Process -Id $smoke.Id; throw 'Owned GameDub smoke process exceeded 45 seconds.' }
$health = Get-Content -LiteralPath 'local-settings/startup-health.json' -Raw | ConvertFrom-Json
$report = [ordered]@{ started=$started.ToString('o'); exitCode=$smoke.ExitCode; ready=$health.ready; runtime=$health.runtime; translationMiB=$health.translationMiB; voiceMiB=$health.voiceMiB }
New-Item -ItemType Directory -Force -Path artifacts | Out-Null
$report | ConvertTo-Json | Set-Content -LiteralPath artifacts/app-smoke.json -Encoding utf8
if ($smoke.ExitCode -ne 0 -or -not $health.ready -or [DateTimeOffset]::Parse($health.at) -lt $started) { throw 'GameDub application startup/shutdown smoke test failed.' }
Write-Output ($report | ConvertTo-Json)
