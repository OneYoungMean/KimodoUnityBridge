@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "SCRIPT_DIR=%~dp0"
set "ROOT_DIR=%SCRIPT_DIR%\..\.."
for %%I in ("%ROOT_DIR%") do set "ROOT_DIR=%%~fI"
set "RESULT_FILE=%~1"
if not defined RESULT_FILE set "RESULT_FILE=%TEMP%\kimodo_case_uv_no_cache_%RANDOM%%RANDOM%.txt"
set "RUN_ROOT=%ROOT_DIR%"
set "TEST_BAT=%ROOT_DIR%\example\example_run_server_tpose.bat"
set "TEST_MODELS_ROOT=%KIMODO_TEST_MODELS_ROOT%"
set "SETUP_BAT=%ROOT_DIR%\run_server.bat"
set "SETUP_LOG=%ROOT_DIR%\log\setup.log"
set "RECYCLE_DIR=%ROOT_DIR%\archive\recycle\case_uv_no_cache"
set "LOCAL_VENV=%ROOT_DIR%\kimodo\.venv"
set "LOCAL_UV_CACHE=%ROOT_DIR%\archive\uv_cache"
set "LOCAL_UV_PYTHON=%ROOT_DIR%\archive\uv_python"
set "LOCAL_TORCH_CACHE=%ROOT_DIR%\archive\torch_wheels"
set "LOCAL_TORCH_PLAN=%ROOT_DIR%\archive\torch_runtime_plan.json"
set "SETUP_SENTINEL=%ROOT_DIR%\.setup.complete"
set "SETUP_LOCK=%ROOT_DIR%\.setup.lock"
set "RUN_MARKER=%ROOT_DIR%\run"

if not exist "%TEST_BAT%" (
  call :write_result FAIL "test_bat_missing"
  exit /b 1
)
if not exist "%SETUP_BAT%" (
  call :write_result FAIL "setup_bat_missing"
  exit /b 1
)

call :prepare_no_cache_env
if errorlevel 1 (
  call :write_result FAIL "prepare_no_cache_env_failed"
  exit /b 1
)

pushd "%RUN_ROOT%" >nul
call "%SETUP_BAT%" setup --force --output file --log "%SETUP_LOG%"
set "SETUP_RC=%ERRORLEVEL%"
popd >nul
if not "%SETUP_RC%"=="0" (
  call :write_result FAIL "setup_failed_rc_%SETUP_RC%"
  exit /b %SETUP_RC%
)

set "KIMODO_VENV_PATH="
if not defined KIMODO_TEST_WAIT_TIMEOUT_SEC set "KIMODO_TEST_WAIT_TIMEOUT_SEC=600"
set "KIMODO_TEST_SERVER_WINDOW_STYLE=Normal"
if defined TEST_MODELS_ROOT set "KIMODO_TEST_MODELS_ROOT=%TEST_MODELS_ROOT%"

pushd "%RUN_ROOT%" >nul
call "%TEST_BAT%"
set "RC=%ERRORLEVEL%"
popd >nul

if not "%RC%"=="0" (
  call :write_result FAIL "tpose_failed_rc_%RC%"
  exit /b %RC%
)

call :write_result PASS "ok"
exit /b 0

:write_result
set "STATUS=%~1"
set "DETAIL=%~2"
> "%RESULT_FILE%" (
  echo CASE_NAME=uv_no_cache
  echo STATUS=%STATUS%
  echo DETAIL=%DETAIL%
  echo RUN_ROOT=%RUN_ROOT%
  echo SETUP_LOG=%SETUP_LOG%
  echo LOCAL_VENV=%LOCAL_VENV%
)
exit /b 0

:prepare_no_cache_env
if not exist "%RECYCLE_DIR%" mkdir "%RECYCLE_DIR%" >nul 2>nul
if errorlevel 1 exit /b 1
call :archive_path "%SETUP_LOG%"
call :archive_path "%SETUP_SENTINEL%"
call :archive_path "%SETUP_LOCK%"
call :archive_path "%RUN_MARKER%"
call :archive_path "%LOCAL_VENV%"
call :archive_path "%LOCAL_UV_CACHE%"
call :archive_path "%LOCAL_UV_PYTHON%"
call :archive_path "%LOCAL_TORCH_CACHE%"
call :archive_path "%LOCAL_TORCH_PLAN%"
exit /b 0

:archive_path
set "ARCHIVE_TARGET=%~1"
if not exist "%ARCHIVE_TARGET%" exit /b 0
for /f %%I in ('powershell -NoProfile -ExecutionPolicy Bypass -Command "(Get-Date).ToString('yyyyMMdd_HHmmss_fff')"') do set "TS=%%I"
set "BASE_NAME=%~nx1"
if not defined BASE_NAME set "BASE_NAME=path"
set "DEST=%RECYCLE_DIR%\%BASE_NAME%.%TS%.%RANDOM%"
move "%ARCHIVE_TARGET%" "%DEST%" >nul 2>nul
if errorlevel 1 (
  echo [ERROR] Failed to archive path: %ARCHIVE_TARGET%
  exit /b 1
)
echo [INFO] Archived %ARCHIVE_TARGET% to %DEST%
exit /b 0
