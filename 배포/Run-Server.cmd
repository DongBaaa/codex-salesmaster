@echo off
setlocal EnableExtensions

set "SERVER_EXE=%~dp0Server\SalesMaster.Server.Api.exe"
if not exist "%SERVER_EXE%" set "SERVER_EXE=%~dp0Server\SalesMaster.Server.exe"
set "APP_SETTINGS=%~dp0App\appsettings.json"
set "SCAN_PORT=19080"

if not exist "%SERVER_EXE%" (
  echo [거래플랜] Server exe not found: %SERVER_EXE%
  pause
  exit /b 1
)

call :FIND_FREE_PORT %SCAN_PORT%
if not defined SERVER_PORT set "SERVER_PORT=19080"
set "SERVER_URL=http://127.0.0.1:%SERVER_PORT%"

if exist "%APP_SETTINGS%" (
  powershell -NoProfile -ExecutionPolicy Bypass -Command "$p='%APP_SETTINGS%'; $u='%SERVER_URL%'; $json=Get-Content -Raw -Path $p | ConvertFrom-Json; if($null -eq $json.Api){$json | Add-Member -NotePropertyName Api -NotePropertyValue ([pscustomobject]@{})}; $json.Api.BaseUrl=$u; $json | ConvertTo-Json -Depth 20 | Set-Content -Path $p -Encoding UTF8"
)

set "ASPNETCORE_URLS=%SERVER_URL%"
echo [거래플랜] Starting server on %SERVER_URL%
"%SERVER_EXE%"
echo.
echo 거래플랜 서버가 종료되었습니다.
pause
exit /b 0

:FIND_FREE_PORT
set "SERVER_PORT=%~1"
if "%SERVER_PORT%"=="" set "SERVER_PORT=19080"
:FIND_FREE_PORT_LOOP
netstat -ano | findstr /R /C:":%SERVER_PORT% .*LISTENING" >nul
if not errorlevel 1 (
  set /a SERVER_PORT+=1
  goto :FIND_FREE_PORT_LOOP
)
exit /b 0
