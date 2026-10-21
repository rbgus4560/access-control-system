@echo off
setlocal
cd /d "%~dp0"
where conda >nul 2>nul
if errorlevel 1 (
 echo Open Anaconda Prompt, then run this file from its extracted folder.
 pause
 exit /b 1
)
call conda create -n access_face_ai -c conda-forge python=3.11 "dlib>=19.24,<21" pip -y
if errorlevel 1 goto failed
call conda run -n access_face_ai python -m pip install -r requirements.txt
if errorlevel 1 goto failed
call conda run -n access_face_ai python -m pip check
if errorlevel 1 goto failed
echo.
echo Environment ready. Run: conda activate access_face_ai
echo Then: python check_setup.py --prepare
echo Then: python face_check.py --camera 0
pause
exit /b 0
:failed
echo Setup stopped. Copy the error text for diagnosis. Existing project is unchanged.
pause
exit /b 1
