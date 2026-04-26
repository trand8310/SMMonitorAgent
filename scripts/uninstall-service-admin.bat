@echo off
setlocal
set SERVICE_NAME=SMMonitorAgent

echo Stopping %SERVICE_NAME%...
sc.exe stop %SERVICE_NAME%
timeout /t 2 >nul

echo Deleting %SERVICE_NAME%...
sc.exe delete %SERVICE_NAME%

echo.
echo Done.
pause
