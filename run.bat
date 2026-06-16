@echo off
setlocal enabledelayedexpansion

set "CONFIG="

:: Parse arguments
:parse_args
if "%~1"=="" goto :done_args
if /i "%~1"=="--config" (
    set "CONFIG=%~2"
    shift
    shift
    goto :parse_args
)
shift
goto :parse_args
:done_args

:: If no config specified, prompt interactively
if "%CONFIG%"=="" (
    echo Select configuration:
    echo   1. Debug
    echo   2. Release
    set /p "CHOICE=Enter choice (1 or 2): "
    if "!CHOICE!"=="2" (
        set "CONFIG=Release"
    ) else (
        set "CONFIG=Debug"
    )
)

:: Normalize config
if /i "%CONFIG%"=="debug" set "CONFIG=Debug"
if /i "%CONFIG%"=="release" set "CONFIG=Release"

set "EXE=Switcheroo\bin\%CONFIG%\switcheroo_plus.exe"

if not exist "%EXE%" (
    echo ERROR: %EXE% not found.
    echo Run build.bat first to compile the project.
    exit /b 1
)

echo Starting switcheroo_plus.exe [%CONFIG%]...
start "" "%EXE%"
