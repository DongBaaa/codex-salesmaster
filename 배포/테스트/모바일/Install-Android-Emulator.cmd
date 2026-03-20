@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Android-Emulator.ps1" -ArtifactDirectory "%~dp0."
if errorlevel 1 (
  echo Android emulator install failed.
  pause
  exit /b 1
)
echo APK install complete.
pause
endlocal
