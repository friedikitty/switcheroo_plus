@echo off
setlocal

set "CONFIG=Debug"

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

:: Normalize config
if /i "%CONFIG%"=="debug" set "CONFIG=Debug"
if /i "%CONFIG%"=="release" set "CONFIG=Release"

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
    if %errorlevel%==0 (
        set "MSBUILD=msbuild"
    ) else (
        echo ERROR: MSBuild not found. Install Visual Studio or add MSBuild to PATH.
        exit /b 1
    )
)

echo Building Core.UnitTests [%CONFIG%]...
"%MSBUILD%" Core.UnitTests\Core.UnitTests.csproj -t:Build -p:Configuration=%CONFIG% -verbosity:minimal
if %errorlevel% neq 0 (
    echo BUILD FAILED.
    exit /b %errorlevel%
)

set "NUNIT=packages\NUnit.ConsoleRunner.3.17.0\tools\nunit3-console.exe"
set "TEST_DLL=Core.UnitTests\bin\%CONFIG%\Switcheroo.Core.UnitTests.dll"

if not exist "%NUNIT%" (
    echo ERROR: NUnit console runner not found at %NUNIT%.
    echo Run NuGet package restore first.
    exit /b 1
)

if not exist "%TEST_DLL%" (
    echo ERROR: Test DLL not found at %TEST_DLL%.
    exit /b 1
)

echo Running tests...
"%NUNIT%" "%TEST_DLL%"
exit /b %errorlevel%
