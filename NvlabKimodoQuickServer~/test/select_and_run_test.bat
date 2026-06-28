@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
set "TEST_DIR=%SCRIPT_DIR%"
set "COPY_BAT=%TEST_DIR%\copy_to_test_timestamp.bat"

if not exist "%COPY_BAT%" (
  echo [ERROR] Missing executor: %COPY_BAT%
  exit /b 1
)

set "IDX=0"
echo.
echo ===== Recommended Tests =====
call :add_item "test\cases\case_cpu_prepared_models.bat" "CPU prepared models ^(recommended case^)"
call :add_item "test\cases\case_cpu_from_scratch.bat" "CPU from scratch ^(cold-setup case^)"
call :add_item "test\test_stress_10_generates_menu.bat" "Stress test menu ^(CPU/CUDA^)"
call :add_item "test\test_recovery_matrix_serial.bat" "Recovery matrix ^(serial^)"

echo.
echo ===== Diagnostics =====
call :add_item "test\test_run_server_multi_start.bat" "Repeated start / idempotency"
call :add_item "test\test_run_server_watchdog_params.bat" "Watchdog parameter coverage"
call :add_item "test\test_setup_network_probe.bat" "Setup/download network probe"

echo.
echo ===== Advanced Cases =====
call :add_case_dir "%TEST_DIR%\cases" "case_runner.bat case_cpu_prepared_models.bat case_cpu_from_scratch.bat"

if %IDX% LEQ 0 (
  echo [ERROR] No test entries found.
  exit /b 1
)

echo.
set /p "CHOICE=Enter number: "
if not defined CHOICE (
  echo [ERROR] Empty selection.
  exit /b 1
)
for /f "delims=0123456789" %%A in ("%CHOICE%") do (
  echo [ERROR] Invalid selection: %CHOICE%
  exit /b 1
)

call set "SELECTED_TEST_REL=%%ITEM_REL[%CHOICE%]%%"
if not defined SELECTED_TEST_REL (
  echo [ERROR] Selection out of range: %CHOICE%
  exit /b 1
)

echo [INFO] Selected test: %SELECTED_TEST_REL%
call "%COPY_BAT%" "%SELECTED_TEST_REL%"
exit /b %ERRORLEVEL%

:add_item
set "ITEM_REL=%~1"
set "ITEM_LABEL=%~2"
for %%I in ("%TEST_DIR%\..\%ITEM_REL%") do set "ITEM_PATH=%%~fI"
if not exist "%ITEM_PATH%" exit /b 0
set /a IDX+=1
set "ITEM_REL[%IDX%]=%ITEM_REL%"
echo   [%IDX%] %ITEM_LABEL%
exit /b 0

:add_case_dir
set "LIST_DIR=%~1"
set "SKIP_LIST=%~2"
if not exist "%LIST_DIR%" exit /b 0
for /f "delims=" %%F in ('dir /b /a-d "%LIST_DIR%\*.bat" 2^>nul') do (
  call :should_skip "%%~nxF" "%SKIP_LIST%"
  if errorlevel 1 (
    set /a IDX+=1
    set "ITEM_REL[!IDX!]=test\cases\%%F"
    echo   [!IDX!] case: %%F
  )
)
exit /b 0

:should_skip
set "CHECK_NAME=%~1"
set "SKIP_LIST=%~2"
if not defined SKIP_LIST exit /b 1
for %%S in (%SKIP_LIST%) do (
  if /I "%CHECK_NAME%"=="%%~S" exit /b 0
)
exit /b 1
