@echo off
setlocal
set "PROJECT_ROOT=%~dp0..\..\"
cd /d "%PROJECT_ROOT%"
echo Retention tests use temporary files only. Production recordings and DB are not changed.
dotnet run --project "%PROJECT_ROOT%RetentionChecks\RetentionChecks.csproj"
if errorlevel 1 echo Retention checks failed. Copy this output.
pause
