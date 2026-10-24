@echo off
rem Build PcOptimizer.exe from source on Windows.
rem Requires the .NET SDK (https://dotnet.microsoft.com/download) - only needed for building, not for running.
cd /d "%~dp0src\PcOptimizer"
dotnet build -c Release
if errorlevel 1 goto :eof
echo.
echo Output: %~dp0src\PcOptimizer\bin\Release\net48\PcOptimizer.exe
pause
