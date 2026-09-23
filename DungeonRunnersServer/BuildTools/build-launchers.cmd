@echo off
setlocal
cd /d "%~dp0.."

dotnet publish BuildTools\launcher.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:AssemblyName=Build -o BuildTools\_out\build
if errorlevel 1 (
    echo Build.exe publish failed.
    exit /b 1
)
copy /y BuildTools\_out\build\Build.exe Build.exe >nul

dotnet publish DungeonRunnersServer.csproj -c Release -r win-x64 --self-contained false -p:UseSharedCompilation=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:AssemblyName=Server -o BuildTools\_out\server
if errorlevel 1 (
    echo Server.exe publish failed.
    exit /b 1
)
copy /y BuildTools\_out\server\Server.exe Server.exe >nul

echo Created Build.exe and Server.exe in "%cd%".
endlocal
