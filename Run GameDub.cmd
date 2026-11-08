@echo off
setlocal
set "gameDubExe=%~dp0build\GameDubAI\GameDub.App.exe"
if exist "%~dp0build\GameDubAI-next\GameDub.App.exe" set "gameDubExe=%~dp0build\GameDubAI-next\GameDub.App.exe"
if exist "%~dp0build\GameDubAI-v2\GameDub.App.exe" set "gameDubExe=%~dp0build\GameDubAI-v2\GameDub.App.exe"
if exist "%~dp0build\GameDubAI-claude\GameDub.App.exe" set "gameDubExe=%~dp0build\GameDubAI-claude\GameDub.App.exe"
if not exist "%gameDubExe%" (
  echo GameDub build is missing. Run Setup.ps1 from this folder first.
  pause
  exit /b 1
)
start "" /D "%~dp0" "%gameDubExe%"
