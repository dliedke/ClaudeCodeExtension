@echo off
REM =====================================================================
REM  Run the Claude Code Extension unit test suite.
REM
REM    test.cmd     Build Tests\ClaudeCodeExtension.Tests.csproj and run it
REM                 under vstest.console.exe. Seconds, no Visual Studio.
REM
REM  Anything that needs a running Visual Studio (terminal embedding,
REM  provider round-trip, settings dialog) is tested manually with F5.
REM =====================================================================

setlocal EnableDelayedExpansion

REM --- Tool locations, VS 2026 first then VS 2022 -----------------------
set "VSROOT=C:\Program Files\Microsoft Visual Studio\18\Enterprise"
if not exist "%VSROOT%\MSBuild\Current\Bin\MSBuild.exe" (
    set "VSROOT=C:\Program Files\Microsoft Visual Studio\2022\Enterprise"
)

set "MSBUILD=%VSROOT%\MSBuild\Current\Bin\MSBuild.exe"
set "VSTEST=%VSROOT%\Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe"

set "TESTPROJ=%~dp0Tests\ClaudeCodeExtension.Tests.csproj"
set "TESTDLL=%~dp0Tests\bin\Debug\net472\ClaudeCodeExtension.Tests.dll"

if not exist "%MSBUILD%" (
    echo [ERROR] MSBuild.exe not found.
    exit /b 1
)
if not exist "%VSTEST%" (
    echo [ERROR] vstest.console.exe not found.
    exit /b 1
)

echo Building test project...
"%MSBUILD%" "%TESTPROJ%" -t:Build -p:Configuration=Debug -v:minimal -nologo
if errorlevel 1 (
    echo [ERROR] Test project build failed.
    exit /b 1
)

echo.
echo Running unit tests...
"%VSTEST%" "%TESTDLL%" /Logger:console;verbosity=minimal
if errorlevel 1 (
    echo.
    echo [ERROR] Unit tests failed.
    exit /b 1
)

echo.
echo [OK] Unit tests passed.
endlocal
exit /b 0
