@echo off
setlocal

for %%I in ("%~dp0..") do set "ROOT_DIR=%%~fI"
set "DIST_DIR=%~dp0"
set "APP_PROJ=%ROOT_DIR%\Desktop\거래플랜.Desktop.App\거래플랜.Desktop.App.csproj"
set "SERVER_PROJ=%ROOT_DIR%\Server\거래플랜.Server.Api\거래플랜.Server.Api.csproj"
set "APP_OUT=%DIST_DIR%App"
set "SERVER_OUT=%DIST_DIR%Server"
set "APP_SETTINGS_SRC=%ROOT_DIR%\Desktop\거래플랜.Desktop.App\appsettings.json"
set "APP_SETTINGS=%APP_OUT%\appsettings.json"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo [거래플랜] dotnet CLI not found. Install .NET SDK 8 first.
  pause
  exit /b 1
)

echo [거래플랜] Publishing Desktop app...
taskkill /F /IM "거래플랜.Desktop.App.exe" >nul 2>nul
taskkill /F /IM "거래플랜.App.exe" >nul 2>nul
taskkill /F /IM "거래플랜.Server.Api.exe" >nul 2>nul
taskkill /F /IM "거래플랜.Server.exe" >nul 2>nul

dotnet publish "%APP_PROJ%" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%APP_OUT%"
if errorlevel 1 goto :PUBLISH_FAIL

echo [거래플랜] Publishing Server...
dotnet publish "%SERVER_PROJ%" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true -o "%SERVER_OUT%"
if errorlevel 1 goto :PUBLISH_FAIL

if exist "%APP_SETTINGS_SRC%" (
  copy /Y "%APP_SETTINGS_SRC%" "%APP_SETTINGS%" >nul
)

if exist "%APP_SETTINGS%" (
  powershell -NoProfile -ExecutionPolicy Bypass -Command "$p='%APP_SETTINGS%'; $c=Get-Content -Raw -Path $p; $c=$c -replace 'http://localhost:[0-9]+','http://127.0.0.1:19080'; $c=$c -replace 'http://127.0.0.1:[0-9]+','http://127.0.0.1:19080'; Set-Content -Path $p -Value $c -Encoding UTF8"
)

echo [거래플랜] Publish complete. Starting app...
call "%DIST_DIR%Run-All.cmd"
exit /b 0

:PUBLISH_FAIL
echo [거래플랜] Publish failed. Fix build errors and retry.
pause
exit /b 1
