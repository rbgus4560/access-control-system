@echo off
setlocal
cd /d "%~dp0"
echo AccessControl 12.3 - two-PC role selection
choice /c SC /m "S: server/admin PC   C: zone/camera client PC"
if errorlevel 2 goto client
call "%~dp0Start_Server.cmd"
exit /b %errorlevel%
:client
call "%~dp0Start_Client.cmd"
