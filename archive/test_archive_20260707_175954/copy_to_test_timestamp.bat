@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
set "SOURCE_DIR=%SCRIPT_DIR%\.."
for %%I in ("%SOURCE_DIR%") do set "SOURCE_DIR=%%~fI"
set "TARGET_ROOT=%SOURCE_DIR%\recycle\test"
set "TEST_BAT_REL=%~1"
if not defined TEST_BAT_REL set "TEST_BAT_REL=%KIMODO_COPY_TEST_BAT_REL%"
set "DEFAULT_SHARED_MODELS_SRC=C:\nvlab\models"
set "DEFAULT_SHARED_MODELS_ALT=C:\nvlab\models~"
set "REQUESTED_TEST_MODELS_ROOT=%KIMODO_COPY_TEST_MODELS_ROOT%"
set "USE_SHARED_MODELS=%KIMODO_COPY_USE_SHARED_MODELS%"
set "TEST_MODELS_ROOT="
set "COPY_ONLY=%KIMODO_COPY_ONLY%"
if not defined COPY_ONLY set "COPY_ONLY=0"
set "COPY_DEST_FILE=%KIMODO_COPY_DEST_FILE%"

if not exist "%SOURCE_DIR%" (
  echo [ERROR] Source directory not found: %SOURCE_DIR%
  exit /b 1
)
if not defined TEST_BAT_REL (
  set "TEST_BAT_REL=example\example_run_server_tpose.bat"
)
if not defined USE_SHARED_MODELS call :infer_shared_models_mode "%TEST_BAT_REL%"
if not exist "%TARGET_ROOT%" (
  mkdir "%TARGET_ROOT%" >nul 2>nul
  if errorlevel 1 (
    echo [ERROR] Failed to create target root: %TARGET_ROOT%
    exit /b 1
  )
)

for /f %%I in ('powershell -NoProfile -ExecutionPolicy Bypass -Command "(Get-Date).ToString('yyyyMMdd_HHmmss_fff')"') do set "TS=%%I"
set "DEST_DIR=%TARGET_ROOT%\NvlabKimodoQuickServer_%TS%_%RANDOM%%RANDOM%"
set "DEST_TEST_BAT=%DEST_DIR%\%TEST_BAT_REL%"

mkdir "%DEST_DIR%" >nul 2>nul
if errorlevel 1 (
  echo [ERROR] Failed to create destination: %DEST_DIR%
  exit /b 1
)

echo [INFO] SOURCE=%SOURCE_DIR%
echo [INFO] DEST=%DEST_DIR%
echo [INFO] TEST_BAT_REL=%TEST_BAT_REL%
echo [STEP] Exporting git-tracked files from working tree...
git -C "%SOURCE_DIR%" rev-parse --is-inside-work-tree >nul 2>nul
if errorlevel 1 (
  echo [ERROR] SOURCE is not a git work tree: %SOURCE_DIR%
  exit /b 1
)

pushd "%SOURCE_DIR%" >nul
set "COPY_FAIL="
for /f "usebackq delims=" %%F in (`git ls-files ^| findstr /V /I /R "^program/exe/git/ ^program/exe/uv/ ^program/exe/llama/"`) do (
  if not "%%F"=="" if not defined COPY_FAIL (
    call :copy_relative_file "%%F"
    if errorlevel 1 set "COPY_FAIL=%%F"
  )
)
popd >nul
if defined COPY_FAIL (
  echo [ERROR] Failed to copy tracked file: %COPY_FAIL%
  exit /b 1
)

echo [STEP] Copying local test files not yet tracked by git...
pushd "%SOURCE_DIR%" >nul
set "COPY_FAIL="
for /f "usebackq delims=" %%F in (`git ls-files --others --exclude-standard -- test/*.bat test/*.ps1 test/*.md test/cases/*.bat`) do (
  if not "%%F"=="" if not defined COPY_FAIL (
    call :copy_relative_file "%%F"
    if errorlevel 1 set "COPY_FAIL=%%F"
  )
)
popd >nul
if defined COPY_FAIL (
  echo [ERROR] Failed to copy local test file: %COPY_FAIL%
  exit /b 1
)

echo [STEP] Copying local portable runtimes...
if exist "%SOURCE_DIR%\program\exe\git" (
  robocopy "%SOURCE_DIR%\program\exe\git" "%DEST_DIR%\program\exe\git" /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP >nul
  if errorlevel 8 (
    echo [ERROR] Failed to copy runtime: program\exe\git, robocopy rc=%ERRORLEVEL%
    exit /b 1
  )
)
if exist "%SOURCE_DIR%\program\exe\uv" (
  robocopy "%SOURCE_DIR%\program\exe\uv" "%DEST_DIR%\program\exe\uv" /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP >nul
  if errorlevel 8 (
    echo [ERROR] Failed to copy runtime: program\exe\uv, robocopy rc=%ERRORLEVEL%
    exit /b 1
  )
)
if exist "%SOURCE_DIR%\program\exe\llama" (
  robocopy "%SOURCE_DIR%\program\exe\llama" "%DEST_DIR%\program\exe\llama" /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP >nul
  if errorlevel 8 (
    echo [ERROR] Failed to copy runtime: program\exe\llama, robocopy rc=%ERRORLEVEL%
    exit /b 1
  )
)

echo [OK] Copy complete: %DEST_DIR%
if /I "%USE_SHARED_MODELS%"=="1" (
  call :resolve_test_models_root
  if errorlevel 1 exit /b 1
  echo [INFO] Test models root: !TEST_MODELS_ROOT!
) else (
  echo [INFO] Test models root: ^<unset^>
)

if /I "%COPY_ONLY%"=="1" (
  echo [RESULT] DEST_DIR=%DEST_DIR%
  echo [RESULT] TEST_MODELS_ROOT=%TEST_MODELS_ROOT%
  echo [RESULT] TEST_BAT_REL=%TEST_BAT_REL%
  if defined COPY_DEST_FILE (
    > "%COPY_DEST_FILE%" (
      echo DEST_DIR=%DEST_DIR%
      echo TEST_MODELS_ROOT=%TEST_MODELS_ROOT%
      echo TEST_BAT_REL=%TEST_BAT_REL%
    )
    echo [INFO] Dest info file: %COPY_DEST_FILE%
  )
  exit /b 0
)

if not exist "%DEST_TEST_BAT%" (
  echo [ERROR] Test entry not found after copy: %DEST_TEST_BAT%
  exit /b 1
)

echo [STEP] Running test: %DEST_TEST_BAT%
if defined TEST_MODELS_ROOT (
  set "KIMODO_TEST_MODELS_ROOT=%TEST_MODELS_ROOT%"
) else (
  set "KIMODO_TEST_MODELS_ROOT="
)
set "KIMODO_TEST_SERVER_WINDOW_STYLE=Normal"
rem Default 1200s: the first run downloads the ~2.6GiB cu128 torch wheel from the
rem PyTorch official index (no mirror, by design), which can exceed the old 300s on
rem slower networks. Override via KIMODO_TEST_WAIT_TIMEOUT_SEC for faster/cached runs.
if not defined KIMODO_TEST_WAIT_TIMEOUT_SEC set "KIMODO_TEST_WAIT_TIMEOUT_SEC=1200"
echo [INFO] KIMODO_TEST_SERVER_WINDOW_STYLE=%KIMODO_TEST_SERVER_WINDOW_STYLE%
echo [INFO] KIMODO_TEST_WAIT_TIMEOUT_SEC=%KIMODO_TEST_WAIT_TIMEOUT_SEC%
call "%DEST_TEST_BAT%"
set "TEST_RC=%ERRORLEVEL%"
echo [INFO] Test exit code: %TEST_RC%
exit /b %TEST_RC%

:copy_relative_file
set "REL=%~1"
set "REL=%REL:/=\%"
if not exist "%REL%" exit /b 0
for %%P in ("%DEST_DIR%\%REL%") do (
  if not exist "%%~dpP" mkdir "%%~dpP" >nul 2>nul
)
copy /Y "%REL%" "%DEST_DIR%\%REL%" >nul
if errorlevel 1 exit /b 1
exit /b 0

:resolve_test_models_root
set "TEST_MODELS_ROOT="
if defined REQUESTED_TEST_MODELS_ROOT (
  if exist "%REQUESTED_TEST_MODELS_ROOT%" (
    set "TEST_MODELS_ROOT=%REQUESTED_TEST_MODELS_ROOT%"
    exit /b 0
  )
  echo [ERROR] Requested shared models source not found: %REQUESTED_TEST_MODELS_ROOT%
  exit /b 1
)
if exist "%DEFAULT_SHARED_MODELS_SRC%" (
  set "TEST_MODELS_ROOT=%DEFAULT_SHARED_MODELS_SRC%"
  exit /b 0
)
if exist "%DEFAULT_SHARED_MODELS_ALT%" (
  set "TEST_MODELS_ROOT=%DEFAULT_SHARED_MODELS_ALT%"
  exit /b 0
)
echo [ERROR] Shared models source not found: %DEFAULT_SHARED_MODELS_SRC% or %DEFAULT_SHARED_MODELS_ALT%
exit /b 1

:infer_shared_models_mode
set "USE_SHARED_MODELS=0"
set "INFER_TEST_BAT_REL=%~1"
if /I "%INFER_TEST_BAT_REL%"=="example\example_run_server_tpose.bat" set "USE_SHARED_MODELS=1"
if /I "%INFER_TEST_BAT_REL%"=="test\test_run_server_multi_start.bat" set "USE_SHARED_MODELS=1"
if /I "%INFER_TEST_BAT_REL%"=="test\test_run_server_watchdog_params.bat" set "USE_SHARED_MODELS=1"
if /I "%INFER_TEST_BAT_REL%"=="test\test_stress_10_generates_menu.bat" set "USE_SHARED_MODELS=1"
if /I "%INFER_TEST_BAT_REL%"=="test\cases\case_cpu_prepared_models.bat" set "USE_SHARED_MODELS=1"
if /I "%INFER_TEST_BAT_REL%"=="test\cases\case_cpu_setup_and_run.bat" set "USE_SHARED_MODELS=1"
if /I "%INFER_TEST_BAT_REL%"=="test\cases\case_highvram_soma.bat" set "USE_SHARED_MODELS=1"
exit /b 0
