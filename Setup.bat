@echo off
setlocal enabledelayedexpansion
echo ============================
echo Setting up Python environment
echo ============================

:: Get the directory of this .bat file
set SCRIPT_DIR=%~dp0
cd /d "%SCRIPT_DIR%"

:: Check Python
python --version >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Python not found. Install 3.10-3.13 and add to PATH.
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
call venv\Scripts\activate
if errorlevel 1 (
    echo [ERROR] Failed to activate venv.
    pause
    exit /b
)

:: Upgrade pip
python -m pip install --upgrade pip

:: Detect CUDA
set CUDA_AVAILABLE=0
nvidia-smi >nul 2>&1
if %errorlevel%==0 (
    set CUDA_AVAILABLE=1
)

:: Install PyTorch first
if %CUDA_AVAILABLE%==1 (
    echo Installing CUDA PyTorch...
    pip3 install torch torchvision --index-url https://download.pytorch.org/whl/cu126
) else (
    echo Installing CPU PyTorch from requirements.txt...
    python -m pip install torch
)

:: Install remaining requirements
if exist "%SCRIPT_DIR%requirements.txt" (
    echo Installing remaining dependencies...
    python -m pip install -r "%SCRIPT_DIR%requirements.txt"
) else (
    echo [WARNING] No requirements.txt found
)

echo.
echo Setup complete!
pause
endlocal
