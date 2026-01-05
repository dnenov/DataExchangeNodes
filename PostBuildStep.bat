@echo off
setlocal

set SOL_DIR=%~1
set PLATFORM=%~2
set CONFIG=%~3
set DYNAMO_VERSION=%~4
set DYNAMO_INSTALL_VERSION=%~5

REM Remove trailing backslash from SOL_DIR if present
if "%SOL_DIR:~-1%"=="\" set SOL_DIR=%SOL_DIR:~0,-1%

set SCRIPT_PACKAGE="%SOL_DIR%\extras\prepareDynamoPackage.py"

echo PostBuildStep: Running package creation script...
echo   Solution Dir: %SOL_DIR%
echo   Platform: %PLATFORM%
echo   Config: %CONFIG%
echo   Dynamo Version: %DYNAMO_VERSION%
echo   Install Version: %DYNAMO_INSTALL_VERSION%

py -3 %SCRIPT_PACKAGE% %PLATFORM% %CONFIG% %DYNAMO_VERSION% %DYNAMO_INSTALL_VERSION%
set PYTHON_EXIT=%ERRORLEVEL%

if %PYTHON_EXIT% NEQ 0 (
    echo PostBuildStep: Python script failed with error code %PYTHON_EXIT%
    echo PostBuildStep: This is non-fatal - build will continue
    exit /b 0
)

echo PostBuildStep: Package creation completed successfully
exit /b 0

