@echo off
REM =====================================================================
REM  Deploy the extension into the Visual Studio experimental hive (Exp).
REM
REM    deploy-exp.cmd          Build Debug and deploy to the Exp hive.
REM    deploy-exp.cmd -run     The above, then start devenv /rootsuffix Exp
REM                            with this solution loaded.
REM
REM  F5 from Visual Studio already does the deploy on its own (the csproj
REM  sets DeployExtension for builds inside VS). This script is the
REM  command-line equivalent, and the way to recover when the Exp hive
REM  loses the extension: it retries after rebuilding the hive's
REM  extension cache, which is what "VSSDK1031 ... could not be found"
REM  on a first deploy needs.
REM =====================================================================

setlocal EnableDelayedExpansion

REM --- Tool locations, VS 2026 first then VS 2022 -----------------------
set "VSROOT=C:\Program Files\Microsoft Visual Studio\18\Enterprise"
if not exist "%VSROOT%\MSBuild\Current\Bin\MSBuild.exe" (
    set "VSROOT=C:\Program Files\Microsoft Visual Studio\2022\Enterprise"
)

set "MSBUILD=%VSROOT%\MSBuild\Current\Bin\MSBuild.exe"
set "DEVENV=%VSROOT%\Common7\IDE\devenv.exe"
set "PROJ=%~dp0ClaudeCodeExtension.csproj"
set "SLN=%~dp0ClaudeCodeExtension.sln"

if not exist "%MSBUILD%" (
    echo [ERROR] MSBuild.exe not found.
    exit /b 1
)
if not exist "%DEVENV%" (
    echo [ERROR] devenv.exe not found.
    exit /b 1
)

REM  Deploying over a running experimental instance leaves it on the old
REM  binaries and can fail the copy outright.
tasklist /fi "imagename eq devenv.exe" | find /i "devenv.exe" >nul
if not errorlevel 1 (
    echo [WARN] Visual Studio is running. Close any /rootsuffix Exp instance
    echo        before deploying, or it will keep the previous build loaded.
)

echo Building and deploying to the Exp hive...
"%MSBUILD%" "%PROJ%" -t:Build -p:Configuration=Debug -p:DeployExtension=true -p:VSSDKTargetPlatformRegRootSuffix=Exp -v:minimal -nologo
if errorlevel 1 (
    echo.
    echo [INFO] Deploy failed. Rebuilding the Exp extension cache and retrying...
    "%DEVENV%" /rootsuffix Exp /updateconfiguration
    "%MSBUILD%" "%PROJ%" -t:Build -p:Configuration=Debug -p:DeployExtension=true -p:VSSDKTargetPlatformRegRootSuffix=Exp -v:minimal -nologo
    if errorlevel 1 (
        echo.
        echo [ERROR] Deploy to the Exp hive failed.
        exit /b 1
    )
)

echo.
echo [OK] Deployed to %LocalAppData%\Microsoft\VisualStudio\^<version^>Exp\Extensions\Daniel Carvalho Liedke

if /i "%~1"=="-run" (
    echo Starting the experimental instance...
    start "" "%DEVENV%" /rootsuffix Exp "%SLN%"
)

endlocal
exit /b 0
