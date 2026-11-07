@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"

where git >nul 2>&1
if errorlevel 1 (
    echo ERROR: git was not found in PATH.
    exit /b 1
)
if not exist ".git" (
    echo ERROR: run this script from the repository root.
    exit /b 1
)

for /f "delims=" %%B in ('git rev-parse --abbrev-ref HEAD 2^>nul') do set "BRANCH=%%B"
if /i not "!BRANCH!"=="master" (
    echo ERROR: current branch is "!BRANCH!", expected "master".
    echo Switch to master before tagging a release.
    exit /b 1
)

git diff-index --quiet HEAD --
if errorlevel 1 (
    echo ERROR: tracked files have uncommitted changes.
    echo Commit or stash them before tagging.
    exit /b 1
)

for /f "delims=" %%H in ('git rev-parse HEAD') do set "HEAD_SHA=%%H"
for /f "delims=" %%H in ('git rev-parse --short HEAD') do set "HEAD_SHORT=%%H"

echo Fetching tags from origin...
git fetch --tags origin >nul 2>&1
if errorlevel 1 echo WARNING: could not fetch remote tags; using local tags.

set "LATEST=none"
for /f "delims=" %%T in ('git tag --list "v*" --sort=-v:refname') do (
    if "!LATEST!"=="none" set "LATEST=%%T"
)

echo.
echo ============================================================
echo  Switcheroo Plus release tag
echo ============================================================
echo  Branch      : !BRANCH!
echo  HEAD        : !HEAD_SHA!
echo  Short SHA   : !HEAD_SHORT!
echo  Latest tag  : !LATEST!
echo.
echo  HEAD details:
git --no-pager show -s --format=fuller HEAD
echo.
echo ============================================================
echo.

set "TAG="
set /p "TAG=Version tag to create (vX.Y.Z): "
if not defined TAG (
    echo No version entered. Cancelled.
    exit /b 1
)
set "TAG=!TAG: =!"
echo !TAG!| findstr /r /c:"^v[0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*$" >nul
if errorlevel 1 (
    echo ERROR: version must look like v1.2.3.
    exit /b 1
)

git rev-parse -q --verify "refs/tags/!TAG!" >nul
if not errorlevel 1 (
    echo ERROR: local tag !TAG! already exists.
    exit /b 1
)

set "REMOTE_TAG="
for /f "delims=" %%R in ('git ls-remote --tags origin "refs/tags/!TAG!" 2^>nul') do set "REMOTE_TAG=%%R"
if defined REMOTE_TAG (
    echo ERROR: remote tag !TAG! already exists.
    exit /b 1
)

set "REMOTE_HEAD="
for /f "tokens=1" %%R in ('git ls-remote origin refs/heads/master 2^>nul') do set "REMOTE_HEAD=%%R"
if defined REMOTE_HEAD if /i not "!REMOTE_HEAD!"=="!HEAD_SHA!" (
    echo WARNING: HEAD is not the remote master head. The tag will point to an unpushed commit.
)

echo.
echo About to create annotated tag !TAG! on !HEAD_SHORT! and push ONLY the tag.
echo Master will not be pushed.
set "CONFIRM="
set /p "CONFIRM=Continue? [y/N]: "
if /i not "!CONFIRM!"=="y" if /i not "!CONFIRM!"=="yes" (
    echo Cancelled.
    exit /b 1
)

git tag -a "!TAG!" -m "Switcheroo Plus !TAG!"
if errorlevel 1 (
    echo ERROR: could not create tag !TAG!.
    exit /b 1
)

git push origin "refs/tags/!TAG!"
if errorlevel 1 (
    echo ERROR: tag push failed. Removing the local tag so it can be retried.
    git tag -d "!TAG!" >nul
    exit /b 1
)

echo.
echo Pushed !TAG!.
echo Release workflow: https://github.com/friedikitty/switcheroo_plus/actions/workflows/release.yml
echo Only the tag was pushed; master was left alone.
exit /b 0
