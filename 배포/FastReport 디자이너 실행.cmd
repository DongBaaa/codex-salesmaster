@echo off
setlocal

set "EXE=%LOCALAPPDATA%\외부 리포팅 도구\Community\Designer.exe"
if not exist "%EXE%" (
  echo [외부 리포팅 도구] Designer.exe not found.
  echo Expected path: %EXE%
  pause
  exit /b 1
)

if "%~1"=="" (
  start "" "%EXE%"
) else (
  start "" "%EXE%" "%~1"
)

exit /b 0
