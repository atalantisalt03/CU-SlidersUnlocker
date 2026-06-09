@echo off
setlocal EnableExtensions

set "PROJECT_DIR=%~dp0"
set "GAME_DIR=%~1"

if not defined GAME_DIR if defined CASUALTIES_UNKNOWN_DIR set "GAME_DIR=%CASUALTIES_UNKNOWN_DIR%"

if not defined GAME_DIR (
    for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%PROJECT_DIR%find-game-dir.ps1"`) do (
        if not defined GAME_DIR set "GAME_DIR=%%I"
    )
)

if not defined GAME_DIR (
    echo Could not find Casualties Unknown Demo automatically.
    echo Pass the game folder path as the first argument, or set CASUALTIES_UNKNOWN_DIR.
    echo Example: build-and-install.bat "D:\SteamLibrary\steamapps\common\Casualties Unknown Demo"
    exit /b 1
)

if not exist "%GAME_DIR%\CasualtiesUnknown_Data\Managed\UnityEngine.CoreModule.dll" (
    echo Invalid game folder: "%GAME_DIR%"
    echo Could not find CasualtiesUnknown_Data\Managed\UnityEngine.CoreModule.dll.
    exit /b 1
)

if not exist "%GAME_DIR%\BepInEx\core\BepInEx.dll" (
    echo BepInEx was not found in "%GAME_DIR%\BepInEx\core".
    echo Install BepInEx first, then run this script again.
    exit /b 1
)

set "PLUGIN_DIR=%GAME_DIR%\BepInEx\plugins"

dotnet build "%PROJECT_DIR%UnlockedSliders.csproj" -c Release -p:GameDir="%GAME_DIR%"
if errorlevel 1 exit /b 1

if not exist "%PLUGIN_DIR%" mkdir "%PLUGIN_DIR%"

copy /Y "%PROJECT_DIR%bin\Release\netstandard2.1\UnlockedSliders.dll" "%PLUGIN_DIR%\UnlockedSliders.dll"
if errorlevel 1 exit /b 1

echo Installed UnlockedSliders.dll to "%PLUGIN_DIR%"
