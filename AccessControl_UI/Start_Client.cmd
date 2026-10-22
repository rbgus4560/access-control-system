@echo off
setlocal
cd /d "%~dp0"
echo AccessControl 12.4 - Client PC - .NET 10
dotnet build "%~dp0FaceRecognition\FaceRecognition.csproj" -c Debug
if errorlevel 1 goto failed
start "" "%~dp0FaceRecognition\bin\Debug\net10.0-windows\FaceRecognition.exe"
exit /b 0
:failed
echo Build failed. Copy the error text from this window.
pause
exit /b 1
