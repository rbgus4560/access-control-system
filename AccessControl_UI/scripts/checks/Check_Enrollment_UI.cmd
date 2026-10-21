@echo off
setlocal
set "PROJECT_ROOT=%~dp0..\..\"
cd /d "%PROJECT_ROOT%"
echo Enrollment UI startup check - no camera, AI, or DB operations
dotnet build "%PROJECT_ROOT%FaceEnrollment\FaceEnrollment.csproj" -c Debug
if errorlevel 1 goto failed
start "" /wait "%PROJECT_ROOT%FaceEnrollment\bin\Debug\net10.0-windows\FaceEnrollment.exe" --ui-smoke
if errorlevel 1 goto failed
echo PASS - enrollment window and settings reopen check.
pause
exit /b 0
:failed
echo FAIL - check build output or %LOCALAPPDATA%\AccessControlSystem\enrollment-startup.log
pause
exit /b 1
