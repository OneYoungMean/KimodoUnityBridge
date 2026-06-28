@echo off
setlocal EnableExtensions
set "SCRIPT_DIR=%~dp0"
set "RESULT_FILE=%~1"
if not defined RESULT_FILE set "RESULT_FILE=%TEMP%\kimodo_case_setup_network_bad_%RANDOM%%RANDOM%.txt"
call "%SCRIPT_DIR%case_runner.bat" "setup_network_bad_once" "KIMODO_TEST_INJECT_SETUP_NET_BAD_ONCE" "1" "" "0" "1" "1" "%RESULT_FILE%"
exit /b %ERRORLEVEL%
