@echo off
setlocal EnableExtensions DisableDelayedExpansion

set "SCRIPT_DIR=%~dp0"
set "RESULT_FILE=%~1"
if not defined RESULT_FILE set "RESULT_FILE=%TEMP%\kimodo_case_cpu_manual_stop_%RANDOM%%RANDOM%.txt"

set "TEST_DIR=%SCRIPT_DIR%\.."
set "COPY_BAT=%TEST_DIR%\copy_to_test_timestamp.bat"
set "DEST_INFO_FILE=%TEMP%\kimodo_case_cpu_manual_stop_dest_%RANDOM%%RANDOM%.txt"

set "KIMODO_TEST_DEVICE=cpu"
set "KIMODO_SETUP_DEVICE=cpu"
set "KIMODO_COPY_ONLY=1"
set "KIMODO_COPY_DEST_FILE=%DEST_INFO_FILE%"
call "%COPY_BAT%"
set "COPY_RC=%ERRORLEVEL%"
set "KIMODO_COPY_ONLY="
set "KIMODO_COPY_DEST_FILE="
if not "%COPY_RC%"=="0" goto fail_copy
if not exist "%DEST_INFO_FILE%" goto fail_copy

set "RUN_ROOT="
for /f "usebackq tokens=1,* delims==" %%A in ("%DEST_INFO_FILE%") do (
  if /I "%%A"=="DEST_DIR" set "RUN_ROOT=%%B"
)
if not defined RUN_ROOT goto fail_copy

set "RUN_BAT=%RUN_ROOT%\run_server.bat"
if not exist "%RUN_BAT%" goto fail_copy

set "RUN_LOG=%RUN_ROOT%\log\case_cpu_manual_stop_run.log"
set "PID_FILE=%RUN_ROOT%\log\case_cpu_manual_stop.pid"
set "PORT_FILE=%RUN_ROOT%\serverport"
if not exist "%RUN_ROOT%\log" mkdir "%RUN_ROOT%\log" >nul 2>nul
if exist "%PID_FILE%" move "%PID_FILE%" "%PID_FILE%.old.%RANDOM%" >nul 2>nul
if exist "%PORT_FILE%" move "%PORT_FILE%" "%RUN_ROOT%\archive\recycle\serverport.manual.%RANDOM%" >nul 2>nul

set "PS_LAUNCH=$ErrorActionPreference='Stop'; $rb='%RUN_BAT%'; $rr='%RUN_ROOT%'; $rl='%RUN_LOG%'; $pf='%PID_FILE%'; $args=@('/d','/c','call',$rb,'--model','Kimodo-SOMA-RP-v1','--device','cpu','--output','file','--log',$rl); $p=Start-Process -FilePath 'cmd.exe' -ArgumentList $args -WorkingDirectory $rr -WindowStyle Normal -PassThru; Set-Content -LiteralPath $pf -Value $p.Id -Encoding ASCII"
powershell -NoProfile -ExecutionPolicy Bypass -Command "%PS_LAUNCH%"
if errorlevel 1 goto fail_launch

set "RUN_PID="
for /f "usebackq delims=" %%P in ("%PID_FILE%") do (
  if not defined RUN_PID set "RUN_PID=%%P"
)
if not defined RUN_PID goto fail_launch

timeout /t 5 /nobreak >nul
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='SilentlyContinue'; $pidValue='%RUN_PID%'; if($pidValue -match '^\d+$'){ Stop-Process -Id ([int]$pidValue) -Force -ErrorAction SilentlyContinue }"
timeout /t 2 /nobreak >nul
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='SilentlyContinue'; $root='%RUN_ROOT%'; $targets='cmd.exe','python.exe','powershell.exe','uv.exe','git.exe','git-lfs.exe'; Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like \"*$root*\" -and ($targets -contains $_.Name.ToLowerInvariant()) } | ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue } catch {} }"
set /a CLEAN_WAIT=0
:wait_residual_processes
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='SilentlyContinue'; $root='%RUN_ROOT%'; $targets='cmd.exe','python.exe','powershell.exe','uv.exe','git.exe','git-lfs.exe'; $ps=Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like \"*$root*\" -and ($targets -contains $_.Name.ToLowerInvariant()) }; if($ps){ exit 1 } else { exit 0 }" >nul 2>nul
if not errorlevel 1 goto residual_cleared
timeout /t 1 /nobreak >nul
set /a CLEAN_WAIT+=1
if %CLEAN_WAIT% lss 30 goto wait_residual_processes
:residual_cleared

if not exist "%RUN_ROOT%\archive\recycle" mkdir "%RUN_ROOT%\archive\recycle" >nul 2>nul
if exist "%RUN_ROOT%\.setup.lock" move "%RUN_ROOT%\.setup.lock" "%RUN_ROOT%\archive\recycle\.setup.lock.manual.%RANDOM%%RANDOM%" >nul 2>nul
if exist "%RUN_ROOT%\log\example_run_server_tpose.pid" move "%RUN_ROOT%\log\example_run_server_tpose.pid" "%RUN_ROOT%\archive\recycle\example_run_server_tpose.pid.manual.%RANDOM%%RANDOM%" >nul 2>nul
if exist "%RUN_ROOT%\serverport" move "%RUN_ROOT%\serverport" "%RUN_ROOT%\archive\recycle\serverport.manual.%RANDOM%%RANDOM%" >nul 2>nul

pushd "%RUN_ROOT%" >nul
set "KIMODO_SETUP_DEVICE=cpu"
call "%RUN_ROOT%\run_server.bat" setup --force --output file --log "%RUN_ROOT%\log\setup.log"
set "RECOVER_SETUP_RC=%ERRORLEVEL%"
popd >nul
if not "%RECOVER_SETUP_RC%"=="0" goto fail_setup_recover

set "KIMODO_TEST_WAIT_TIMEOUT_SEC=%KIMODO_TEST_RUN2_WAIT_TIMEOUT_SEC%"
if not defined KIMODO_TEST_WAIT_TIMEOUT_SEC set "KIMODO_TEST_WAIT_TIMEOUT_SEC=600"
set "KIMODO_TEST_MODELS_ROOT="
set "KIMODO_MODELS_ROOT="
set "TEST_BAT=%RUN_ROOT%\example\example_run_server_tpose.bat"
if not exist "%TEST_BAT%" goto fail_launch
pushd "%RUN_ROOT%" >nul
call "%TEST_BAT%"
set "RC2=%ERRORLEVEL%"
popd >nul

if "%RC2%"=="0" (
  > "%RESULT_FILE%" (
    echo CASE_NAME=cpu_manual_stop_once
    echo STATUS=PASS
    echo DETAIL=ok
    echo RUN_ROOT=%RUN_ROOT%
  )
  exit /b 0
)

> "%RESULT_FILE%" (
  echo CASE_NAME=cpu_manual_stop_once
  echo STATUS=FAIL
  echo DETAIL=run2_failed_rc_%RC2%
  echo RUN_ROOT=%RUN_ROOT%
)
exit /b 1

:fail_copy
> "%RESULT_FILE%" (
  echo CASE_NAME=cpu_manual_stop_once
  echo STATUS=FAIL
  echo DETAIL=copy_failed
)
exit /b 1

:fail_launch
> "%RESULT_FILE%" (
  echo CASE_NAME=cpu_manual_stop_once
  echo STATUS=FAIL
  echo DETAIL=launch_failed
  if defined RUN_ROOT echo RUN_ROOT=%RUN_ROOT%
)
exit /b 1

:fail_setup_recover
> "%RESULT_FILE%" (
  echo CASE_NAME=cpu_manual_stop_once
  echo STATUS=FAIL
  echo DETAIL=recover_setup_failed_rc_%RECOVER_SETUP_RC%
  if defined RUN_ROOT echo RUN_ROOT=%RUN_ROOT%
)
exit /b 1
