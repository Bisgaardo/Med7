@echo off
echo ============================
echo Setting up Python environment
echo ============================

:: Check if Python is installed
python --version >nul 2>&1
if errorlevel 1 (
    echo Python is not installed or not on PATH.
    echo Please install Python 3.10 or newer from https://www.python.org/downloads/
    echo And make sure to check "Add Python to PATH" during installation.
    pause
    exit /b
)

:: Create venv if it doesn't exist
if not exist "venv" (
    echo Creating virtual environment...
    python -m venv venv
) else (
    echo Virtual environment already exists.
)

:: Activate venv
echo Activating virtual environment...
call venv\Scripts\activate

:: Upgrade pip (optional but helps)
python -m pip install --upgrade pip

:: Install requirements
if exist "requirements.txt" (
    echo Installing dependencies from requirements.txt...
    python -m pip install -r requirements.txt
) else (
    echo  No requirements.txt file found.
)

echo.
echo Setup complete!
echo.
pause
