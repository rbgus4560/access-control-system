@echo off
setlocal
cd /d "%~dp0"
echo Retention tests use temporary files only. Production recordings and DB are not changed.
dotnet run --project "%~dp0RetentionChecks\RetentionChecks.csproj"
if errorlevel 1 echo Retention checks failed. Copy this output.
pause
