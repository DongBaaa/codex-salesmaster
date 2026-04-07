@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Deploy-After-Test.ps1" %*
set EXIT_CODE=%ERRORLEVEL%
if not "%EXIT_CODE%"=="0" (
    echo.
    echo Deploy script failed. Check the log above.
    pause
)
endlocal & exit /b %EXIT_CODE%
