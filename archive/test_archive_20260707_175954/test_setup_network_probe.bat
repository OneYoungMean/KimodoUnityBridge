@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
set "ROOT_DIR=%SCRIPT_DIR%\.."
for %%I in ("%ROOT_DIR%") do set "ROOT_DIR=%%~fI"
set "UV_EXE=%ROOT_DIR%\program\exe\uv\uv.exe"

if not exist "%UV_EXE%" (
  echo [ERROR] Missing local uv: %UV_EXE%
  exit /b 1
)

set "MODE=%~1"
if not defined MODE set "MODE=quick"
if /I not "%MODE%"=="quick" if /I not "%MODE%"=="full" (
  echo [ERROR] MODE must be quick or full.
  exit /b 1
)

set "TORCH_BACKEND=%~2"
if not defined TORCH_BACKEND set "TORCH_BACKEND=cu128"
if /I "%TORCH_BACKEND%"=="auto" set "TORCH_BACKEND=cu128"
if /I not "%TORCH_BACKEND%"=="cpu" if /I not "%TORCH_BACKEND%"=="cu128" (
  echo [ERROR] TORCH_BACKEND must be cpu, cu128, or auto.
  exit /b 1
)

set "NO_CACHE=%~3"
if not defined NO_CACHE set "NO_CACHE=0"

set "INDEX_URL=%KIMODO_PIP_INDEX_URL%"
if not defined INDEX_URL set "INDEX_URL=https://pypi.tuna.tsinghua.edu.cn/simple"

for /f "delims=" %%I in ('"%UV_EXE%" --version 2^>nul') do (
  if not defined UV_VERSION set "UV_VERSION=%%I"
)

set "PYTHON_EXE="
for /f "usebackq delims=" %%I in (`"%UV_EXE%" python find 3.12 2^>nul`) do (
  if not defined PYTHON_EXE set "PYTHON_EXE=%%I"
)
if not defined PYTHON_EXE (
  echo [ERROR] Failed to locate Python via uv python find 3.12
  exit /b 1
)

set "STAMP=%RANDOM%%RANDOM%_%TIME:~0,2%%TIME:~3,2%%TIME:~6,2%"
set "STAMP=%STAMP: =0%"
set "STAMP=%STAMP::=%"
set "WORK_ROOT=%ROOT_DIR%\recycle\uv_probe"
set "OUT_DIR=%WORK_ROOT%\probe_%STAMP%_%RANDOM%%RANDOM%"
set "TARGET_DIR=%OUT_DIR%\target"
set "CACHE_DIR=%OUT_DIR%\cache"
set "LOG_DIR=%OUT_DIR%\logs"
set "REPORT=%OUT_DIR%\report.txt"

if not exist "%WORK_ROOT%" mkdir "%WORK_ROOT%" >nul 2>nul
if not exist "%OUT_DIR%" mkdir "%OUT_DIR%" >nul 2>nul
if not exist "%TARGET_DIR%" mkdir "%TARGET_DIR%" >nul 2>nul
if not exist "%LOG_DIR%" mkdir "%LOG_DIR%" >nul 2>nul
if not "%NO_CACHE%"=="1" if not exist "%CACHE_DIR%" mkdir "%CACHE_DIR%" >nul 2>nul

set "UV_NO_CONFIG=1"
if "%NO_CACHE%"=="1" (
  set "CACHE_FLAGS=--no-cache"
) else (
  set "UV_CACHE_DIR=%CACHE_DIR%"
  set "CACHE_FLAGS="
)

> "%REPORT%" (
  echo Setup UV Download Probe
  echo =======================
  echo root=%ROOT_DIR%
  echo mode=%MODE%
  echo torch_backend=%TORCH_BACKEND%
  echo no_cache=%NO_CACHE%
  echo index=%INDEX_URL%
  echo uv=%UV_EXE%
  echo uv_version=%UV_VERSION%
  echo python=%PYTHON_EXE%
  echo out_dir=%OUT_DIR%
  echo.
)

echo [INFO] report=%REPORT%
echo [INFO] mode=%MODE% backend=%TORCH_BACKEND% no_cache=%NO_CACHE%

set "STEP_FAILED=0"

set "STEP_NAME=uv_helpers_install"
set "STEP_LOG=%LOG_DIR%\01_helpers.log"
set "STEP_CMD="%UV_EXE%" pip install --python "%PYTHON_EXE%" --target "%TARGET_DIR%\helpers" --default-index "%INDEX_URL%" --no-config --link-mode copy %CACHE_FLAGS% packaging pip setuptools wheel"
call :run_step

set "STEP_NAME=uv_runtime_deps_install"
set "STEP_LOG=%LOG_DIR%\02_runtime_deps.log"
set "STEP_CMD="%UV_EXE%" pip install --python "%PYTHON_EXE%" --target "%TARGET_DIR%\runtime_deps" --default-index "%INDEX_URL%" --no-config --link-mode copy %CACHE_FLAGS% tqdm huggingface_hub safetensors"
call :run_step

set "STEP_NAME=uv_torch_dry_run"
set "STEP_LOG=%LOG_DIR%\03_torch_dry_run.log"
set "STEP_CMD="%UV_EXE%" pip install --python "%PYTHON_EXE%" --target "%TARGET_DIR%\torch_plan" --default-index "%INDEX_URL%" --no-config %CACHE_FLAGS% --dry-run --torch-backend %TORCH_BACKEND% torch torchvision torchaudio"
call :run_step

if /I "%MODE%"=="full" (
  set "STEP_NAME=uv_torch_full_install"
  set "STEP_LOG=%LOG_DIR%\04_torch_full_install.log"
  set "STEP_CMD="%UV_EXE%" pip install --python "%PYTHON_EXE%" --target "%TARGET_DIR%\torch_full" --default-index "%INDEX_URL%" --no-config --link-mode copy %CACHE_FLAGS% --torch-backend %TORCH_BACKEND% torch torchvision torchaudio"
  call :run_step
)

>> "%REPORT%" echo Summary
>> "%REPORT%" echo -------
if defined uv_helpers_install_duration_cs (
  call :maybe_warn_small uv_helpers_install "build helpers"
)
if defined uv_runtime_deps_install_duration_cs (
  call :maybe_warn_small uv_runtime_deps_install "runtime deps"
)
if defined uv_torch_dry_run_duration_cs (
  call :maybe_warn_dry uv_torch_dry_run
)
if defined uv_torch_full_install_duration_cs (
  call :maybe_warn_torch uv_torch_full_install
)
if "%STEP_FAILED%"=="0" (
  >> "%REPORT%" echo overall=PASS
  echo [OK] probe passed
  echo [INFO] report=%REPORT%
  exit /b 0
)

>> "%REPORT%" echo overall=FAIL
echo [WARN] probe has failures, see %REPORT%
exit /b 1

:run_step
call :now_cs STEP_START_CS
call :timestamp STEP_START_TS
echo [STEP] %STEP_NAME%
>> "%REPORT%" echo [STEP] %STEP_NAME%
>> "%REPORT%" echo start=%STEP_START_TS%
>> "%REPORT%" echo command=%STEP_CMD%
call %STEP_CMD% > "%STEP_LOG%" 2>&1
set "STEP_RC=%ERRORLEVEL%"
call :now_cs STEP_END_CS
set /a STEP_DURATION_CS=STEP_END_CS-STEP_START_CS
if !STEP_DURATION_CS! lss 0 set /a STEP_DURATION_CS+=8640000
call :format_cs !STEP_DURATION_CS! STEP_DURATION_TEXT
echo [INFO] %STEP_NAME% rc=!STEP_RC! duration=!STEP_DURATION_TEXT!
>> "%REPORT%" echo rc=!STEP_RC!
>> "%REPORT%" echo duration=!STEP_DURATION_TEXT!
>> "%REPORT%" echo log=%STEP_LOG%
>> "%REPORT%" echo.
set "%STEP_NAME%_duration_cs=!STEP_DURATION_CS!"
set "%STEP_NAME%_rc=!STEP_RC!"
if not "!STEP_RC!"=="0" set "STEP_FAILED=1"
exit /b 0

:maybe_warn_small
set "STEP_KEY=%~1"
set "STEP_LABEL=%~2"
call set "STEP_VAL=%%%STEP_KEY%_duration_cs%%"
if not defined STEP_VAL exit /b 0
if %STEP_VAL% GEQ 3000 (
  >> "%REPORT%" echo warning=%STEP_LABEL% took more than 30s, index or small-package download may be slow.
)
exit /b 0

:maybe_warn_dry
set "STEP_KEY=%~1"
call set "STEP_VAL=%%%STEP_KEY%_duration_cs%%"
if not defined STEP_VAL exit /b 0
if %STEP_VAL% GEQ 2000 (
  >> "%REPORT%" echo warning=torch dry-run took more than 20s, resolver or index metadata may be slow.
)
exit /b 0

:maybe_warn_torch
set "STEP_KEY=%~1"
call set "STEP_VAL=%%%STEP_KEY%_duration_cs%%"
if not defined STEP_VAL exit /b 0
if %STEP_VAL% GEQ 6000 (
  >> "%REPORT%" echo warning=real torch install took more than 60s, large wheel download is likely the main bottleneck.
)
exit /b 0

:timestamp
set "%~1=%date% %time%"
exit /b 0

:now_cs
setlocal
set "T=%time: =0%"
for /f "tokens=1-4 delims=:., " %%a in ("%T%") do (
  set /a "H=1%%a-100"
  set /a "M=1%%b-100"
  set /a "S=1%%c-100"
  set /a "C=1%%d-100"
)
set /a "TOTAL=((H*60+M)*60+S)*100+C"
endlocal & set "%~1=%TOTAL%"
exit /b 0

:format_cs
setlocal
set /a "TOTAL=%~1"
if %TOTAL% lss 0 set /a "TOTAL+=8640000"
set /a "TOTAL_SEC=TOTAL/100"
set /a "CS=TOTAL%%100"
set /a "SS=TOTAL_SEC%%60"
set /a "TOTAL_MIN=TOTAL_SEC/60"
set /a "MM=TOTAL_MIN%%60"
set /a "HH=TOTAL_MIN/60"
if %HH% lss 10 set "HH=0%HH%"
if %MM% lss 10 set "MM=0%MM%"
if %SS% lss 10 set "SS=0%SS%"
if %CS% lss 10 set "CS=0%CS%"
set "TEXT=%HH%:%MM%:%SS%.%CS%"
endlocal & set "%~2=%TEXT%"
exit /b 0
