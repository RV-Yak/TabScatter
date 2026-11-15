@echo off
setlocal
cd /d "%~dp0"

dotnet publish "TabScatter.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o "%~dp0publish"
if errorlevel 1 (
    echo Publishing failed. The publish folder may be incomplete.
    pause
    exit /b 1
)

echo.
echo App created in: %~dp0publish
echo Keep Links.txt beside TabScatter.exe when copying the folder.
pause
