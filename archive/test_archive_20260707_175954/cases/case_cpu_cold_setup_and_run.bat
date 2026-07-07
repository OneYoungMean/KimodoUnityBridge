@echo off
setlocal EnableExtensions

set "SCRIPT_DIR=%~dp0"
set "RESULT_FILE=%~1"
if not defined RESULT_FILE set "RESULT_FILE=%TEMP%\kimodo_case_cpu_cold_setup_%RANDOM%%RANDOM%.txt"

set "KIMODO_TEST_DEVICE=cpu"
set "KIMODO_SETUP_DEVICE=cpu"
set "KIMODO_TEST_GENERATE_WAIT_MINUTES=90"

call "%SCRIPT_DIR%case_runner.bat" "cpu_cold_setup_and_run" "" "" "" "0" "0" "0" "%RESULT_FILE%"
exit /b %ERRORLEVEL%
