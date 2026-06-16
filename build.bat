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
    echo Select build configuration:
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

echo Building Switcheroo.sln [%CONFIG%]...

:: Find MSBuild
set "MSBUILD="
if exist "%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" (
    set "MSBUILD=%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
) else if exist "%ProgramFiles%\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" (
    set "MSBUILD=%ProgramFiles%\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"
) else if exist "%ProgramFiles%\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe" (
    set "MSBUILD=%ProgramFiles%\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
) else (
    where msbuild >nul 2>&1
    if !errorlevel!==0 (
        set "MSBUILD=msbuild"
    ) else (
        echo ERROR: MSBuild not found. Install Visual Studio or add MSBuild to PATH.
        exit /b 1
    )
)

"%MSBUILD%" Switcheroo.sln -t:Build -p:Configuration=%CONFIG% -verbosity:minimal
if %errorlevel% neq 0 (
    echo BUILD FAILED.
    exit /b %errorlevel%
)

echo Build succeeded [%CONFIG%].
