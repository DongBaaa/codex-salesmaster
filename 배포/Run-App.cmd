@echo off
setlocal
set "APP_EXE=%~dp0App\거래플랜.Desktop.App.exe"
if not exist "%APP_EXE%" set "APP_EXE=%~dp0App\거래플랜.App.exe"
if not exist "%APP_EXE%" (
  echo [거래플랜] App exe not found: %APP_EXE%
  pause
  exit /b 1
)
start "거래플랜 App" "%APP_EXE%"
