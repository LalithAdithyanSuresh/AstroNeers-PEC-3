@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

echo ==========================================
echo       AstroBOT Environment Setup
echo ==========================================

:: --- STEP 0: CHECK & INSTALL PYTHON 3.10 ---
echo [0/5] Checking for Python 3.10...

:: Check if py -3.10 or python (version 3.10) is available
py -3.10 --version >nul 2>&1
if %errorlevel% equ 0 goto :PythonFound

python --version 2>nul | findstr "3.10" >nul
if %errorlevel% equ 0 goto :PythonFound

echo [INFO] Python 3.10 not found. Starting automatic installation...
echo [INFO] Downloading Python 3.10.11 Installer...

:: 1. Download the installer
curl -L -o python_installer.exe https://www.python.org/ftp/python/3.10.11/python-3.10.11-amd64.exe

if not exist python_installer.exe (
    echo [ERROR] Download failed. Please check your internet connection.
    pause
    exit /b
)

echo [INFO] Launching Python Installer...
echo [IMPORTANT] 1. Make sure "Add Python to PATH" is CHECKED.
echo [IMPORTANT] 2. Click "Install Now".
echo [IMPORTANT] 3. Wait for it to finish.

:: 2. Run Installer with UI (Removed /quiet)
:: PrependPath=1 tries to pre-check the "Add to PATH" box for you.
start /wait python_installer.exe PrependPath=1 Include_test=0

:: 3. Cleanup
del python_installer.exe

echo.
echo [SUCCESS] Installer finished.
echo [IMPORTANT] Windows needs to refresh to see the new Python version.
echo Please CLOSE this window and run 'setup.bat' again.
pause
exit

:PythonFound
echo [OK] Python 3.10 is available.

:: --- STEP 1: VERIFY FILES ---
echo.
echo [1/5] Verifying project files...
if not exist "Python\requirements.txt" (
    echo [WARN] Python\requirements.txt missing! Creating default...
    if not exist "Python" mkdir Python
    (
        echo flask
        echo ruamel.yaml
        echo mlagents
    ) > Python\requirements.txt
)
if not exist "Python\results" mkdir Python\results

:: --- STEP 2: VIRTUAL ENVIRONMENT ---
echo.
echo [2/5] Handling Virtual Environment...
if not exist "venv\Scripts\activate.bat" (
    echo Creating venv...
    :: Try using 'py' launcher first, fallback to 'python'
    py -3.10 -m venv venv || python -m venv venv
)

:: --- STEP 3: INSTALL DEPENDENCIES ---
echo.
echo [3/5] Syncing dependencies...
call venv\Scripts\activate.bat
python -m pip install --upgrade pip

:: 3a. Install PyTorch with CUDA support explicitly
echo [INFO] Installing PyTorch (CUDA 12.1)...
pip install torch==2.2.2+cu121 -f https://download.pytorch.org/whl/torch_stable.html

:: 3b. Install the rest of the requirements
echo [INFO] Installing remaining dependencies...
if exist "Python\requirements.txt" (
    pip install -r Python\requirements.txt
) else (
    echo [ERROR] requirements.txt not found.
)

:: --- STEP 4: LAUNCH SERVER ---
echo.
echo [4/5] Launching Mission Control Server...
:: Opens a new window, activates venv, moves to server folder, runs app
start "AstroBOT SERVER" cmd /k "call .\venv\Scripts\activate.bat && cd Python\server && python app.py"

:: --- STEP 5: LAUNCH WORKSPACE ---
echo.
echo [5/5] Opening Workspace Terminal...
start "AstroBOT TERMINAL" cmd /k "call .\venv\Scripts\activate.bat && echo Environment Ready. Type 'mlagents-learn' to start."

echo ==========================================
echo        Setup Complete! 
echo        Dashboard: http://127.0.0.1:5000
echo ==========================================
pause