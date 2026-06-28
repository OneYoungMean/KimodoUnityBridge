@echo off
setlocal EnableExtensions

set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%\..\..") do set "ROOT_DIR=%%~fI"
set "RESULT_FILE=%~1"
if not defined RESULT_FILE set "RESULT_FILE=%TEMP%\kimodo_test_cpu_prepared_%RANDOM%%RANDOM%.txt"
if not defined UV_CACHE_DIR set "UV_CACHE_DIR=%ROOT_DIR%\.uv-cache"
if not defined KIMODO_VENV_PATH set "KIMODO_VENV_PATH=%ROOT_DIR%\kimodo\.venv"

echo [INFO] CPU prepared-models test
echo [INFO] Requires prebuilt assets under C:\nvlab\models~
echo [INFO] Running case_cpu_setup_and_run.bat

call "%SCRIPT_DIR%\case_cpu_setup_and_run.bat" "%RESULT_FILE%"
exit /b %ERRORLEVEL%
