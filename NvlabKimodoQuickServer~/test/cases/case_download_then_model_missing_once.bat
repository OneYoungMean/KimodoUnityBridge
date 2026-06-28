@echo off
setlocal EnableExtensions
set "SCRIPT_DIR=%~dp0"
set "RESULT_FILE=%~1"
if not defined RESULT_FILE set "RESULT_FILE=%TEMP%\kimodo_case_download_missing_%RANDOM%%RANDOM%.txt"
set "KIMODO_TEST_RUN1_WAIT_TIMEOUT_SEC=60"
set "KIMODO_TEST_RUN2_WAIT_TIMEOUT_SEC=1200"
set "KIMODO_TEST_GENERATE_WAIT_MINUTES=90"
call "%SCRIPT_DIR%case_runner.bat" "download_then_model_missing_once" "KIMODO_TEST_INJECT_MODEL_MISSING_AFTER_DOWNLOAD_ONCE" "1" "" "0" "0" "1" "%RESULT_FILE%"
exit /b %ERRORLEVEL%
