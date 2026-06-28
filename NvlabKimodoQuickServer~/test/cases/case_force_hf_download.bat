@echo off
setlocal EnableExtensions
set "SCRIPT_DIR=%~dp0"
set "RESULT_FILE=%~1"
if not defined RESULT_FILE set "RESULT_FILE=%TEMP%\kimodo_case_force_hf_download_%RANDOM%%RANDOM%.txt"
set "KIMODO_TEST_FORCE_HF_DOWNLOAD=1"
call "%SCRIPT_DIR%case_runner.bat" "force_hf_download" "" "" "" "0" "0" "0" "%RESULT_FILE%"
exit /b %ERRORLEVEL%
