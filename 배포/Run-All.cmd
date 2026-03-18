@echo off
setlocal EnableExtensions

set "SERVER_EXE=%~dp0Server\SalesMaster.Server.Api.exe"
if not exist "%SERVER_EXE%" set "SERVER_EXE=%~dp0Server\SalesMaster.Server.exe"
set "APP_EXE=%~dp0App\SalesMaster.Desktop.App.exe"
if not exist "%APP_EXE%" set "APP_EXE=%~dp0App\SalesMaster.App.exe"
set "APP_SETTINGS=%~dp0App\appsettings.json"
set "SCAN_PORT=19080"
set /a PORT_TRIES=0

if not exist "%SERVER_EXE%" (
  echo [거래플랜] Server exe not found: %SERVER_EXE%
  pause
  exit /b 1
)

if not exist "%APP_EXE%" (
  echo [거래플랜] App exe not found: %APP_EXE%
  pause
  exit /b 1
)

taskkill /F /IM "SalesMaster.Desktop.App.exe" >nul 2>nul
taskkill /F /IM "SalesMaster.App.exe" >nul 2>nul
taskkill /F /IM "SalesMaster.Server.Api.exe" >nul 2>nul
taskkill /F /IM "SalesMaster.Server.exe" >nul 2>nul

:TRY_START_SERVER
set /a PORT_TRIES+=1
if %PORT_TRIES% GTR 10 goto :SERVER_FAIL

call :FIND_FREE_PORT %SCAN_PORT%
if not defined SERVER_PORT goto :SERVER_FAIL
set "SERVER_URL=http://127.0.0.1:%SERVER_PORT%"

echo [거래플랜] Using server URL %SERVER_URL%

if exist "%APP_SETTINGS%" (
  powershell -NoProfile -ExecutionPolicy Bypass -Command "$p='%APP_SETTINGS%'; $u='%SERVER_URL%'; $json=Get-Content -Raw -Path $p | ConvertFrom-Json; if($null -eq $json.Api){$json | Add-Member -NotePropertyName Api -NotePropertyValue ([pscustomobject]@{})}; $json.Api.BaseUrl=$u; $json | ConvertTo-Json -Depth 20 | Set-Content -Path $p -Encoding UTF8"
)

echo [거래플랜] Starting server on %SERVER_URL%...
set "ASPNETCORE_URLS=%SERVER_URL%"
start "거래플랜 Server" "%SERVER_EXE%"
call :WAIT_FOR_PORT %SERVER_PORT% 20
if errorlevel 1 (
  echo [거래플랜] Server did not bind to %SERVER_URL%. Retrying with next port...
  set /a SCAN_PORT=%SERVER_PORT%+1
  goto :TRY_START_SERVER
)

echo [거래플랜] Running server smoke test...
call :VERIFY_HTTP "%SERVER_URL%"
if errorlevel 1 (
  echo [거래플랜] Server smoke test failed at %SERVER_URL%. Retrying with next port...
  taskkill /F /IM "SalesMaster.Server.Api.exe" >nul 2>nul
  taskkill /F /IM "SalesMaster.Server.exe" >nul 2>nul
  set /a SCAN_PORT=%SERVER_PORT%+1
  goto :TRY_START_SERVER
)

echo [거래플랜] Starting app...
start "거래플랜 App" "%APP_EXE%"
exit /b 0

:SERVER_FAIL
echo [거래플랜] Failed to start server after multiple attempts.
echo [거래플랜] Run "%~dp0Run-Server.cmd" to check detailed server errors.
pause
exit /b 1

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

:WAIT_FOR_PORT
set "WAIT_PORT=%~1"
set /a WAIT_REMAIN=%~2
if "%WAIT_PORT%"=="" exit /b 1
if %WAIT_REMAIN% LEQ 0 set /a WAIT_REMAIN=20
:WAIT_FOR_PORT_LOOP
netstat -ano | findstr /R /C:":%WAIT_PORT% .*LISTENING" >nul
if not errorlevel 1 exit /b 0
set /a WAIT_REMAIN-=1
if %WAIT_REMAIN% LEQ 0 exit /b 1
ping 127.0.0.1 -n 2 >nul
goto :WAIT_FOR_PORT_LOOP

:VERIFY_HTTP
set "CHECK_URL=%~1"
if "%CHECK_URL%"=="" exit /b 1
powershell -NoProfile -ExecutionPolicy Bypass -Command "$u='%CHECK_URL%/'; try { Invoke-WebRequest -Uri $u -UseBasicParsing -TimeoutSec 5 | Out-Null; exit 0 } catch { if ($_.Exception.Response -and $_.Exception.Response.StatusCode) { $c = [int]$_.Exception.Response.StatusCode; if ($c -ge 100 -and $c -lt 600) { exit 0 } }; exit 1 }"
exit /b %errorlevel%
