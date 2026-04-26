@echo off
setlocal

REM 请用管理员权限运行本脚本
set SERVICE_NAME=SMMonitorAgent
set BASE_DIR=%~dp0\..\publish\service
set EXE=%BASE_DIR%\SMMonitor.Agent.Service.exe

if not exist "%EXE%" (
    echo Service exe not found: %EXE%
    echo Please run publish-win-x64.bat first.
    pause
    exit /b 1
)

echo Installing %SERVICE_NAME%...
sc.exe create %SERVICE_NAME% binPath= "%EXE%" start= auto DisplayName= "SMMonitorAgent"
sc.exe description %SERVICE_NAME% "System resource monitor agent"
sc.exe failure %SERVICE_NAME% reset= 60 actions= restart/5000/restart/5000/restart/10000
sc.exe start %SERVICE_NAME%

echo.
echo Done.
pause
