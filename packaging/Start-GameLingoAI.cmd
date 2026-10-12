@echo off
setlocal EnableExtensions DisableDelayedExpansion
if not exist "%~dp0app\GameLingoAI.App.exe" (
  echo Extract the entire ZIP before starting GameLingoAI.
  exit /b 1
)
set "GAMELINGOAI_DATA_ROOT=%~dp0data"
set "TEMP=%GAMELINGOAI_DATA_ROOT%\temp"
set "TMP=%TEMP%"
if not exist "%TEMP%\" mkdir "%TEMP%"
if errorlevel 1 (
  echo Cannot create the data directory. Extract to a writable local folder.
  exit /b 1
)
start "" /wait "%~dp0app\GameLingoAI.App.exe"
exit /b %errorlevel%
