@echo off
setlocal

:: ── Configuration ────────────────────────────────────────────────────────────
set PROJECT=SESpriteLCDLayoutTool\SESpriteLCDLayoutTool.csproj
set OUT_DIR=SESpriteLCDLayoutTool\bin\Release
set RELEASE_DIR=release

:: Read version from AssemblyInfo.cs
for /f "tokens=2 delims=()" %%v in (
    'findstr /R "AssemblyVersion" SESpriteLCDLayoutTool\Properties\AssemblyInfo.cs'
) do set RAW_VER=%%v
:: Strip quotes and trim to major.minor.patch
for /f "tokens=1-3 delims=." %%a in ("%RAW_VER:"=%") do set VERSION=%%a.%%b.%%c

set ZIP_NAME=SESpriteLCDLayoutTool-v%VERSION%.zip

echo.
echo  ╔══════════════════════════════════════════════╗
echo  ║  SE Sprite LCD Layout Tool — Release Build   ║
echo  ╚══════════════════════════════════════════════╝
echo.
echo  Version : %VERSION%
echo  Output  : %RELEASE_DIR%\%ZIP_NAME%
echo.

:: ── Build ────────────────────────────────────────────────────────────────────
echo [1/3] Building Release...
msbuild "%PROJECT%" /p:Configuration=Release /t:Rebuild /v:minimal /nologo
if errorlevel 1 (
    echo.
    echo  *** BUILD FAILED ***
    exit /b 1
)
echo       Build OK.
echo.

:: ── Verify output ────────────────────────────────────────────────────────────
if not exist "%OUT_DIR%\SESpriteLCDLayoutTool.exe" (
    echo  *** EXE not found in %OUT_DIR% ***
    exit /b 1
)

:: ── Package ──────────────────────────────────────────────────────────────────
echo [2/3] Packaging...
if not exist "%RELEASE_DIR%" mkdir "%RELEASE_DIR%"
if exist "%RELEASE_DIR%\%ZIP_NAME%" del "%RELEASE_DIR%\%ZIP_NAME%"

:: Create a staging folder with only the files we want in the zip
set STAGE=%RELEASE_DIR%\_stage
if exist "%STAGE%" rd /s /q "%STAGE%"
mkdir "%STAGE%"

copy "%OUT_DIR%\SESpriteLCDLayoutTool.exe"        "%STAGE%\" >nul
copy "%OUT_DIR%\SESpriteLCDLayoutTool.exe.config"  "%STAGE%\" >nul 2>nul
copy "%OUT_DIR%\Scintilla.NET.dll"                 "%STAGE%\" >nul
copy "%OUT_DIR%\Scintilla.dll"                     "%STAGE%\" >nul
copy "%OUT_DIR%\Lexilla.dll"                       "%STAGE%\" >nul
copy "README.md"                                   "%STAGE%\" >nul
copy "LICENSE"                                     "%STAGE%\" >nul
copy "setup.ps1"                                   "%STAGE%\" >nul

:: Zip using PowerShell (available on all modern Windows)
powershell -NoProfile -Command "Compress-Archive -Path '%STAGE%\*' -DestinationPath '%RELEASE_DIR%\%ZIP_NAME%' -Force"
if errorlevel 1 (
    echo  *** ZIP FAILED ***
    exit /b 1
)

:: Clean up staging
rd /s /q "%STAGE%"

echo       Packaged OK.
echo.

:: ── Done ─────────────────────────────────────────────────────────────────────
echo [3/3] Done!
echo.
echo  ──────────────────────────────────────────────
echo   %RELEASE_DIR%\%ZIP_NAME%
echo  ──────────────────────────────────────────────
echo.
echo  Ready to upload to GitHub Releases.
