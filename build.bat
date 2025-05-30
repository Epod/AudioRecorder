@echo off
echo Building Multi-Source Audio Recorder...
echo.

REM Check if .NET 6 SDK is installed
dotnet --version >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: .NET 6 SDK is not installed or not in PATH
    echo Please install .NET 6 SDK from: https://dotnet.microsoft.com/download/dotnet/6.0
    pause
    exit /b 1
)

REM Clean previous builds
if exist "bin" rmdir /s /q "bin"
if exist "obj" rmdir /s /q "obj"

echo Restoring NuGet packages...
dotnet restore
if %errorlevel% neq 0 (
    echo ERROR: Failed to restore packages
    pause
    exit /b 1
)

echo.
echo Building application...
dotnet build --configuration Release
if %errorlevel% neq 0 (
    echo ERROR: Build failed
    pause
    exit /b 1
)

REM Get Git tag for version
echo.
echo Checking for Git tag version...
for /f "tokens=*" %%i in ('git describe --tags --exact-match 2^>nul') do set GIT_TAG=%%i
if defined GIT_TAG (
    echo Found Git tag: %GIT_TAG%
    set VERSION_PARAM=/p:GitTag=%GIT_TAG%
) else (
    echo No Git tag found, using default version
    set VERSION_PARAM=
)

echo.
echo Publishing Framework-dependent (smaller) executable...
dotnet publish --configuration Release --runtime win-x64 --self-contained false --output "./dist-small" /p:PublishSingleFile=true %VERSION_PARAM%
if %errorlevel% neq 0 (
    echo ERROR: Framework-dependent publishing failed
    pause
    exit /b 1
)

echo.
echo Publishing Self-contained (portable) executable...
dotnet publish --configuration Release --runtime win-x64 --self-contained true --output "./dist-portable" /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true %VERSION_PARAM%
if %errorlevel% neq 0 (
    echo ERROR: Self-contained publishing failed
    pause
    exit /b 1
)

echo.
echo SUCCESS: Both builds completed!
echo.
echo Two versions have been created:
echo.
echo 1. SMALL VERSION (Framework-dependent):
echo    - Location: dist-small\AudioRecorder.exe
echo    - Size: ~15-25MB
echo    - Requires: .NET 6 Desktop Runtime on target machine
echo    - Download runtime: https://dotnet.microsoft.com/download/dotnet/6.0
echo.
echo 2. PORTABLE VERSION (Self-contained):
echo    - Location: dist-portable\AudioRecorder.exe
echo    - Size: ~130-150MB
echo    - Requires: Nothing (all dependencies included)
echo    - Can run on any Windows 10+ machine
echo.
if defined GIT_TAG (
    echo Version: %GIT_TAG%
) else (
    echo Version: 1.0.0 (default)
)
echo.
pause
