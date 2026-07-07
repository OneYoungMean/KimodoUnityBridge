@echo off
setlocal EnableExtensions
set "SCRIPT_DIR=%~dp0"
set "RESULT_FILE=%~1"
if not defined RESULT_FILE set "RESULT_FILE=%TEMP%\kimodo_case_download_network_bad_%RANDOM%%RANDOM%.txt"
call "%SCRIPT_DIR%case_runner.bat" "download_network_bad_once" "KIMODO_TEST_INJECT_DOWNLOAD_NET_BAD_ONCE" "1" "" "0" "0" "1" "%RESULT_FILE%"
exit /b %ERRORLEVEL%
