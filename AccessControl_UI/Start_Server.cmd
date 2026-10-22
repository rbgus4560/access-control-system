@echo off
setlocal
cd /d "%~dp0"
echo AccessControl 12.4 - Server PC - .NET 10
dotnet build "%~dp0AccessControlServer\AccessControlServer.csproj" -c Debug
if errorlevel 1 goto failed
dotnet build "%~dp0FaceEnrollment\FaceEnrollment.csproj" -c Debug
if errorlevel 1 goto failed
start "" "%~dp0AccessControlServer\bin\Debug\net10.0-windows\AccessControlServer.exe"
exit /b 0
:failed
echo Build failed. Copy the error text from this window.
pause
exit /b 1
