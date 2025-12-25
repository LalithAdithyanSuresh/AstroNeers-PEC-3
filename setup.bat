@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

echo ==========================================
echo       AstroBOT Environment Setup
echo ==========================================

:: 1. Check for Required Project Files
echo [1/5] Verifying project files...
set "MISSING_FILES=0"
if not exist "Python\requirements.txt" (echo [WARN] Python\requirements.txt missing! & set MISSING_FILES=1)
if not exist "UnityProject" (echo [WARN] Unity folder not found! & set MISSING_FILES=1)

:: 2. Virtual Environment Setup
echo.
echo [2/5] Creating/Checking virtual environment (Python 3.10)...
if not exist ".\venv\Scripts\activate.bat" (
    py -3.10 -m venv venv
)

:: 3. Activate and Install
call .\venv\Scripts\activate.bat
echo [3/5] Upgrading pip and installing requirements...
python -m pip install --upgrade pip
if exist "Python\requirements.txt" (
    pip install -r Python\requirements.txt
)

:: 4. Check for Unity Hub/Editor
echo.
echo [4/5] Checking for Unity...
where unity.exe >nul 2>&1
if %errorlevel% neq 0 (
    echo [ERROR] Unity.exe not found in PATH. Please ensure Unity is installed.
) else (
    echo [OK] Unity detected.
)

:: 5. Launch Terminal and Unity
echo.
echo [5/5] Launching Development Environment...
echo Starting Unity Project...
:: Replace "UnityProject" with your actual Unity folder name
start "" "unity.exe" -projectPath "%~dp0UnityProject"

echo Opening Terminal in venv...
start cmd /k "echo AstroBOT Shell Activated && call .\venv\Scripts\activate.bat"

echo ==========================================
echo        Setup Complete! Ready to work.
echo ==========================================
pause