@echo off
setlocal EnableExtensions
set "SCRIPT_DIR=%~dp0"
set "RESULT_FILE=%~1"
if not defined RESULT_FILE set "RESULT_FILE=%TEMP%\kimodo_case_cpu_crash_%RANDOM%%RANDOM%.txt"
set "KIMODO_TEST_DEVICE=cpu"
call "%SCRIPT_DIR%case_runner.bat" "cpu_crash_once" "KIMODO_TEST_INJECT_SETUP_ABORT_ONCE" "1" "" "0" "0" "1" "%RESULT_FILE%"
exit /b %ERRORLEVEL%

