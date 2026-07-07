@echo off
setlocal EnableExtensions
set "SCRIPT_DIR=%~dp0"
set "RESULT_FILE=%~1"
if not defined RESULT_FILE set "RESULT_FILE=%TEMP%\kimodo_case_highvram_soma_%RANDOM%%RANDOM%.txt"
call "%SCRIPT_DIR%case_runner.bat" "highvram_soma" "" "" "Kimodo-SOMA-RP-v1" "1" "1" "0" "%RESULT_FILE%"
exit /b %ERRORLEVEL%
