@echo off
setlocal
cd /d %~dp0\..

if not exist publish mkdir publish

echo Publishing SMMonitor.Agent.Service...
dotnet publish src\SMMonitor.Agent.Service\SMMonitor.Agent.Service.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o publish\service
if errorlevel 1 goto error

echo Publishing SMMonitor.Agent.Manager...
dotnet publish src\SMMonitor.Agent.Manager\SMMonitor.Agent.Manager.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o publish\manager
if errorlevel 1 goto error

echo.
echo Publish completed.
echo Service: publish\service\SMMonitor.Agent.Service.exe
echo Manager: publish\manager\SMMonitor.Agent.Manager.exe
pause
exit /b 0

:error
echo Publish failed.
pause
exit /b 1
