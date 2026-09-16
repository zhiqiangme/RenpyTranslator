@echo off
rem Launch the translation JSONL viewer without a console window
rem Prefer the system Python 3.15 (has Tkinter), fall back to pythonw/python on PATH
cd /d "%~dp0"
set "PY315=%LOCALAPPDATA%\Programs\Python\Python315\pythonw.exe"
if exist "%PY315%" (
    start "" "%PY315%" translation_viewer.py
    exit /b
)
where pythonw >nul 2>&1
if not errorlevel 1 (
    start "" pythonw translation_viewer.py
    exit /b
)
where python >nul 2>&1
if not errorlevel 1 (
    python translation_viewer.py
    exit /b
)
echo Python not found. Please install Python 3 with Tkinter from python.org.
pause
