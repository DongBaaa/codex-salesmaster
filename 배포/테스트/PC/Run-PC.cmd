@echo off
setlocal
cd /d "%~dp0"
if exist "%~dp0SalesMaster.Desktop.App.exe" (
  start "" "%~dp0SalesMaster.Desktop.App.exe"
  exit /b 0
)
for %%F in ("%~dp0*Desktop.App.exe") do (
  if exist "%%~fF" (
    start "" "%%~fF"
    exit /b 0
  )
)
echo GeoraePlan desktop executable not found.
exit /b 1
