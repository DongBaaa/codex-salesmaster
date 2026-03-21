@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\tools\mobile\Start-GeoraePlanAndroidStudioTest.ps1" -ProjectRoot "%~dp0.." -SigningConfigPath "%~dp0..\Mobile\GeoraePlan.Mobile.App\android-signing.local.json"
if errorlevel 1 (
  echo Android Studio mobile test launch failed.
  pause
  exit /b 1
)
echo Android Studio mobile test is ready.
pause
endlocal
