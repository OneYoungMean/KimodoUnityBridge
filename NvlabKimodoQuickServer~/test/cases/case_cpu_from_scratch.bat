@echo off
setlocal EnableExtensions

set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%\..\..") do set "ROOT_DIR=%%~fI"
set "RESULT_FILE=%~1"
if not defined RESULT_FILE set "RESULT_FILE=%TEMP%\kimodo_test_cpu_from_scratch_%RANDOM%%RANDOM%.txt"
if not defined UV_CACHE_DIR set "UV_CACHE_DIR=%ROOT_DIR%\.uv-cache"

echo [INFO] CPU from-scratch test
echo [INFO] Uses an isolated copied workspace with no shared models-root
echo [INFO] Running case_cpu_cold_setup_and_run.bat

call "%SCRIPT_DIR%\case_cpu_cold_setup_and_run.bat" "%RESULT_FILE%"
exit /b %ERRORLEVEL%
