@echo off
setlocal
set "APP_EXE=%~dp0App\SalesMaster.Desktop.App.exe"
if not exist "%APP_EXE%" set "APP_EXE=%~dp0App\SalesMaster.App.exe"
if not exist "%APP_EXE%" (
  echo [SalesMaster] App exe not found: %APP_EXE%
  pause
  exit /b 1
)
start "SalesMaster App" "%APP_EXE%"
