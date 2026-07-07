@echo off
setlocal EnableExtensions
set "SCRIPT_DIR=%~dp0"
set "RESULT_FILE=%~1"
if not defined RESULT_FILE set "RESULT_FILE=%TEMP%\kimodo_case_model_smplx_%RANDOM%%RANDOM%.txt"
set "KIMODO_TEST_RUN1_WAIT_TIMEOUT_SEC=1200"
call "%SCRIPT_DIR%case_runner.bat" "model_variant_smplx_rp" "" "" "Kimodo-SMPLX-RP-v1" "0" "0" "0" "%RESULT_FILE%"
exit /b %ERRORLEVEL%
