@echo off
echo ============================
echo Generating clean requirements.txt (removing CUDA suffixes)
echo ============================

:: Use the virtual environment’s Python
set "VENV_PY=%~dp0venv\Scripts\python.exe"

if not exist "%VENV_PY%" (
    echo Virtual environment not found!
    echo Please ensure "venv" exists next to this .bat file.
    pause
    exit /b
)

:: Create temporary raw requirements file
echo Freezing packages...
"%VENV_PY%" -m pip freeze > "%~dp0requirements_raw.txt"

:: Remove CUDA suffixes (e.g. +cu121, +cu126, etc.)
echo Cleaning CUDA suffixes...
powershell -Command "(Get-Content '%~dp0requirements_raw.txt') -replace '\+cu\d+', '' | Set-Content '%~dp0requirements.txt'"

:: Delete temp file
del "%~dp0requirements_raw.txt"

echo ============================
echo Done! Clean requirements.txt created.
echo ============================
pause