@echo off
setlocal
set SERVICE_NAME=SMMonitorAgent

sc.exe stop %SERVICE_NAME%
timeout /t 3 >nul
sc.exe start %SERVICE_NAME%

pause
