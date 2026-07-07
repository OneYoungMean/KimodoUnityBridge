@echo off
setlocal EnableExtensions DisableDelayedExpansion

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
set "CASES_DIR=%SCRIPT_DIR%\cases"
set "RESULT_DIR=%SCRIPT_DIR%\results"
set "LOG_DIR=%SCRIPT_DIR%\log"
set "MATRIX_LOG=%LOG_DIR%\recovery_matrix_serial.log"
set "PASS_COUNT=0"
set "FAIL_COUNT=0"
set "FAILED="
set "ONLY_SCENARIO=%KIMODO_MATRIX_ONLY%"
set "CASE_TIMEOUT_SEC=%KIMODO_MATRIX_CASE_TIMEOUT_SEC%"
if not defined CASE_TIMEOUT_SEC set "CASE_TIMEOUT_SEC=1800"
set /a CASE_TIMEOUT_MS=%CASE_TIMEOUT_SEC%*1000
if defined ONLY_SCENARIO for /f "usebackq delims=" %%A in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$v=$env:KIMODO_MATRIX_ONLY; if($null -eq $v){''} else {$v.Trim()}"`) do set "ONLY_SCENARIO=%%A"

if not exist "%CASES_DIR%" (
  echo [ERROR] cases dir missing: %CASES_DIR%
  exit /b 1
)
if not exist "%RESULT_DIR%" mkdir "%RESULT_DIR%" >nul 2>nul
if not exist "%LOG_DIR%" mkdir "%LOG_DIR%" >nul 2>nul

> "%MATRIX_LOG%" echo [MATRIX] start %DATE% %TIME%
call :log "[MATRIX] mode=serial timeout_run1_sec=60 timeout_run2_sec=600 case_timeout_sec=%CASE_TIMEOUT_SEC% visible_windows=on"
if defined ONLY_SCENARIO call :log "[MATRIX] only_scenario=%ONLY_SCENARIO%"

set "CASE_LIST=case_download_interrupt_once case_download_network_bad_once case_download_then_model_missing_once case_setup_interrupt_once case_setup_network_bad_once case_model_variant_g1_rp case_model_variant_smplx_rp case_model_variant_soma_seed case_local_tools_uv_git case_cpu_crash_once case_cpu_network_bad_once case_cpu_manual_stop_once"

for %%C in (%CASE_LIST%) do (
  if defined ONLY_SCENARIO (
    if /I not "%ONLY_SCENARIO%"=="%%C" (
      rem skip
    ) else (
      call :run_case_serial "%%C"
    )
  ) else (
    call :run_case_serial "%%C"
  )
)

call :log "[SUMMARY] pass=%PASS_COUNT% fail=%FAIL_COUNT%"
if defined FAILED call :log "[SUMMARY] failed=%FAILED%"
if %FAIL_COUNT% GTR 0 exit /b 1
exit /b 0

:run_case_serial
set "CASE_NAME=%~1"
set "CASE_BAT=%CASES_DIR%\%CASE_NAME%.bat"
set "RESULT_FILE=%RESULT_DIR%\%CASE_NAME%.result"
set "PID_FILE=%RESULT_DIR%\%CASE_NAME%.pid"
if not exist "%CASE_BAT%" (
  call :log "[ERROR] missing case bat: %CASE_BAT%"
  > "%RESULT_FILE%" (
    echo CASE_NAME=%CASE_NAME%
    echo STATUS=FAIL
    echo DETAIL=case_script_missing
  )
  exit /b 0
)
if exist "%RESULT_FILE%" move "%RESULT_FILE%" "%RESULT_FILE%.old.%RANDOM%" >nul 2>nul
if exist "%PID_FILE%" move "%PID_FILE%" "%PID_FILE%.old.%RANDOM%" >nul 2>nul
call :log "[CASE] %CASE_NAME% launched"
set "PS_CMD=$ErrorActionPreference='Stop'; $case='%CASE_BAT%'; $res='%RESULT_FILE%'; $pidf='%PID_FILE%'; $env:KIMODO_TEST_RUN1_WAIT_TIMEOUT_SEC='60'; $env:KIMODO_TEST_RUN2_WAIT_TIMEOUT_SEC='600'; $p=Start-Process -FilePath 'cmd.exe' -ArgumentList @('/d','/c',$case,$res) -WindowStyle Normal -PassThru; Set-Content -LiteralPath $pidf -Value $p.Id -Encoding ASCII; if(-not $p.WaitForExit(%CASE_TIMEOUT_MS%)){ try{ Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch {}; exit 124 }; exit $p.ExitCode"
powershell -NoProfile -ExecutionPolicy Bypass -Command "%PS_CMD%"
set "RUN_RC=%ERRORLEVEL%"
if "%RUN_RC%"=="124" (
  > "%RESULT_FILE%" (
    echo CASE_NAME=%CASE_NAME%
    echo STATUS=FAIL
    echo DETAIL=timeout_%CASE_TIMEOUT_SEC%s
  )
) else if not "%RUN_RC%"=="0" (
  if not exist "%RESULT_FILE%" (
    > "%RESULT_FILE%" (
      echo CASE_NAME=%CASE_NAME%
      echo STATUS=FAIL
      echo DETAIL=case_exit_%RUN_RC%
    )
  )
)
if not exist "%RESULT_FILE%" call :wait_for_result "%RESULT_FILE%" 10

set "STATUS=FAIL"
set "DETAIL=result_missing"
if exist "%RESULT_FILE%" (
  for /f "usebackq tokens=1,* delims==" %%A in ("%RESULT_FILE%") do (
    if /I "%%A"=="STATUS" set "STATUS=%%B"
    if /I "%%A"=="DETAIL" set "DETAIL=%%B"
  )
)
if /I "%STATUS%"=="PASS" (
  set /a PASS_COUNT+=1
  call :log "[CASE] %CASE_NAME% PASS"
) else (
  set /a FAIL_COUNT+=1
  if defined FAILED (
    set "FAILED=%FAILED%,%CASE_NAME%"
  ) else (
    set "FAILED=%CASE_NAME%"
  )
  call :log "[CASE] %CASE_NAME% FAIL: %DETAIL%"
)
exit /b 0

:wait_for_result
set "W_RESULT_FILE=%~1"
set "W_TIMEOUT=%~2"
if not defined W_TIMEOUT set "W_TIMEOUT=5"
set /a W_ELAPSED=0
:wait_result_loop
if exist "%W_RESULT_FILE%" exit /b 0
if "%W_ELAPSED%" geq "%W_TIMEOUT%" exit /b 0
ping 127.0.0.1 -n 2 >nul
set /a W_ELAPSED+=1
goto wait_result_loop

:log
echo %~1
>> "%MATRIX_LOG%" echo %~1
exit /b 0
