@echo off
setlocal
cd /d "%~dp0.."
set "distributionScratch=%~dp0..\..\Temp\distribution-build"

dotnet publish BuildTools\launcher.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:AssemblyName=Build --artifacts-path "%distributionScratch%\artifacts" -o "%distributionScratch%\launchers"
if errorlevel 1 (
    echo Build.exe publish failed.
    exit /b 1
)
copy /y "%distributionScratch%\launchers\Build.exe" Build.exe >nul

dotnet publish DungeonRunnersServer.csproj -c Release -r win-x64 --self-contained false -p:UseSharedCompilation=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:AssemblyName=Server --artifacts-path "%distributionScratch%\artifacts" -o "%distributionScratch%\server"
if errorlevel 1 (
    echo Server.exe publish failed.
    exit /b 1
)
copy /y "%distributionScratch%\server\Server.exe" Server.exe >nul

echo Created Build.exe and Server.exe in "%cd%".
endlocal
