@echo off
setlocal
cd /d "%~dp0"
dotnet run --project "TabScatter.csproj"
if errorlevel 1 pause
