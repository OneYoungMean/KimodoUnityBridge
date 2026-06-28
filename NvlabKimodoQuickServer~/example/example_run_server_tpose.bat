@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%.") do set "SCRIPT_DIR=%%~fI"
if "!SCRIPT_DIR:~-1!"=="\" set "SCRIPT_DIR=!SCRIPT_DIR:~0,-1!"
for %%I in ("!SCRIPT_DIR!\..") do set "ROOT_DIR=%%~fI"
for %%I in ("!ROOT_DIR!\run_server.bat") do set "LAUNCHER=%%~fI"
for %%I in ("!SCRIPT_DIR!\example_run_server_tpose_client.ps1") do set "CLIENT_PS1=%%~fI"
set "PORT_FILE=!ROOT_DIR!\serverport"
set "PID_FILE=!ROOT_DIR!\log\example_run_server_tpose.pid"
set "BRIDGE_SERVER_LOG=!ROOT_DIR!\log\bridge_server.log"
set "WATCHDOG_LOG=!ROOT_DIR!\log\watchdog.log"
set "RECYCLE_DIR=!ROOT_DIR!\archive\recycle"
set "WAIT_TIMEOUT_SEC=%KIMODO_TEST_WAIT_TIMEOUT_SEC%"
if not defined WAIT_TIMEOUT_SEC set "WAIT_TIMEOUT_SEC=600"
set "WAIT_HINT_INTERVAL_SEC=10"
rem Grace window after run_server exits before declaring failure. run_server exits
rem normally once it hands off to the bridge, so we wait a few seconds for the
rem bridge endpoint to appear; if it does not, the startup failed and we bail out
rem immediately instead of waiting out WAIT_TIMEOUT_SEC. Override via env if needed.
set "RUNSERVER_EXIT_GRACE_SEC=%KIMODO_TEST_RUNSERVER_EXIT_GRACE_SEC%"
if not defined RUNSERVER_EXIT_GRACE_SEC set "RUNSERVER_EXIT_GRACE_SEC=15"

set "MODEL=Kimodo-SOMA-RP-v1"
if defined KIMODO_TEST_MODEL set "MODEL=%KIMODO_TEST_MODEL%"
set "FORCE_HF_DOWNLOAD=0"
if /I "%KIMODO_TEST_FORCE_HF_DOWNLOAD%"=="1" set "FORCE_HF_DOWNLOAD=1"
rem Device policy: only an explicit CPU request is forwarded as --device cpu (it is
rem a mode switch that drives setup to install the cpu torch build and pins the
rem local INT8 text encoder to CPU). Otherwise DEVICE stays empty and we do NOT pass --device at
rem all -- run_server/bridge then auto-select via torch.cuda.is_available()
rem (GPU when present, CPU fallback when not). Passing "cuda" explicitly is avoided
rem because it would bypass that auto-detection and crash on CPU-only machines.
set "DEVICE="
if /I "%KIMODO_TEST_DEVICE%"=="cpu" set "DEVICE=cpu"
set "MODELS_ROOT=%KIMODO_TEST_MODELS_ROOT%"

if not exist "!LAUNCHER!" (
  echo [ERROR] run_server.bat not found: !LAUNCHER!
  exit /b 1
)
if not exist "!CLIENT_PS1!" (
  echo [ERROR] client ps1 not found: !CLIENT_PS1!
  exit /b 1
)
if not exist "!ROOT_DIR!\log" mkdir "!ROOT_DIR!\log" >nul 2>nul
if not exist "!RECYCLE_DIR!" mkdir "!RECYCLE_DIR!" >nul 2>nul

set "KIMODO_SETUP_DEVICE="
if defined KIMODO_TEST_DEVICE (
  if /I "%KIMODO_TEST_DEVICE%"=="cpu" set "KIMODO_SETUP_DEVICE=cpu"
)

echo [TEST] ROOT_DIR=!ROOT_DIR!
echo [TEST] MODEL=!MODEL!
if defined DEVICE (
  echo [TEST] DEVICE=!DEVICE!
) else (
  echo [TEST] DEVICE=^<auto^>
)
if defined MODELS_ROOT (
  echo [TEST] MODELS_ROOT=!MODELS_ROOT!
) else (
  echo [TEST] MODELS_ROOT=^<default^>
)
echo [TEST] FORCE_HF_DOWNLOAD=!FORCE_HF_DOWNLOAD!
if defined KIMODO_VENV_PATH (
  echo [TEST] VENV_PATH=!KIMODO_VENV_PATH!
) else (
  echo [TEST] VENV_PATH=^<default^>
)
echo [TEST] OUTPUT=file

call :archive_file "%PORT_FILE%"
call :archive_file "%PID_FILE%"
if exist "!BRIDGE_SERVER_LOG!" call :archive_file "!BRIDGE_SERVER_LOG!"

set "OWNER_PID_CMD=$ownerPidValue = $PID; try { $parentPidValue = (Get-CimInstance Win32_Process -Filter ('ProcessId=' + $PID) | Select-Object -ExpandProperty ParentProcessId); if($parentPidValue){ $ownerPidValue = $parentPidValue } } catch {}"
set "LAUNCH_PS_CMD=$ErrorActionPreference='Stop'; %OWNER_PID_CMD%; $args=@('/d','/c','!LAUNCHER!','--model','%MODEL%','--watchpid',[string]$ownerPidValue);"
if defined DEVICE call set "LAUNCH_PS_CMD=%%LAUNCH_PS_CMD%% $args += @('--device','%DEVICE%');"
if defined MODELS_ROOT call set "LAUNCH_PS_CMD=%%LAUNCH_PS_CMD%% $args += @('--models-root','%MODELS_ROOT%');"
if "%FORCE_HF_DOWNLOAD%"=="1" call set "LAUNCH_PS_CMD=%%LAUNCH_PS_CMD%% $args += @('--force-hf-download');"
call set "LAUNCH_PS_CMD=%%LAUNCH_PS_CMD%% $args += @('--output','file','--log','%BRIDGE_SERVER_LOG%');"
set "LAUNCH_PS_CMD=!LAUNCH_PS_CMD! $p=Start-Process -FilePath 'cmd.exe' -ArgumentList $args -WorkingDirectory '%ROOT_DIR%' -WindowStyle Normal -PassThru; Set-Content -LiteralPath '%PID_FILE%' -Value $p.Id -Encoding ASCII"

powershell -NoProfile -ExecutionPolicy Bypass -Command "!LAUNCH_PS_CMD!"
if errorlevel 1 (
  echo [ERROR] failed to launch run_server.
  exit /b 1
)

set /a WAIT_SEC=0
:wait_serverport
if exist "!PORT_FILE!" (
  call :read_serverport_retry
  if not errorlevel 1 goto got_serverport
)
call :read_endpoint_from_logs
if not errorlevel 1 goto got_serverport
call :is_runserver_alive
if errorlevel 1 (
  rem run_server has exited. This is normal once it hands the service off to the
  rem bridge process, so we do NOT fail immediately: the bridge may still be
  rem writing serverport/endpoint. But if run_server died WITHOUT a successful
  rem handoff (e.g. setup/preflight failed), the endpoint will never appear, so
  rem we only grant a short grace window after exit before declaring failure --
  rem far faster than waiting out the full WAIT_TIMEOUT_SEC.
  if not defined RUNSERVER_EXITED (
    set "RUNSERVER_EXITED=1"
    set /a RUNSERVER_EXIT_AT=WAIT_SEC
  )
  set /a GRACE_ELAPSED=WAIT_SEC-RUNSERVER_EXIT_AT
  if !GRACE_ELAPSED! geq %RUNSERVER_EXIT_GRACE_SEC% (
    echo [ERROR] run_server exited and no serverport appeared within %RUNSERVER_EXIT_GRACE_SEC%s grace; setup/startup likely failed.
    call :dump_startup_logs
    exit /b 1
  )
)
ping 127.0.0.1 -n 2 >nul
set /a WAIT_SEC+=1
set /a WAIT_MOD=WAIT_SEC %% WAIT_HINT_INTERVAL_SEC
if !WAIT_MOD! equ 0 (
  if defined RUNSERVER_EXITED (
    echo [TEST] waiting serverport... !WAIT_SEC!/%WAIT_TIMEOUT_SEC%s ^(run_server exited, waiting for bridge handoff^)
  ) else (
    echo [TEST] waiting serverport... !WAIT_SEC!/%WAIT_TIMEOUT_SEC%s
  )
)
if !WAIT_SEC! geq %WAIT_TIMEOUT_SEC% (
  if defined RUNSERVER_EXITED (
    echo [ERROR] serverport missing after !WAIT_SEC!s: !PORT_FILE! ^(run_server exited during startup^)
  ) else (
    echo [ERROR] serverport missing after !WAIT_SEC!s: !PORT_FILE!
  )
  call :dump_startup_logs
  exit /b 1
)
goto wait_serverport

:got_serverport
if not defined HOST (
  echo [ERROR] endpoint host is empty.
  call :dump_startup_logs
  exit /b 1
)
if not defined PORT (
  echo [ERROR] endpoint port is empty.
  call :dump_startup_logs
  exit /b 1
)
echo [TEST] TARGET=!HOST!:!PORT!

powershell -NoProfile -ExecutionPolicy Bypass -File "%CLIENT_PS1%" -HostName "!HOST!" -Port !PORT! -Prompt "tpose" -Duration 5.0 -Seed 42 -DiffusionSteps 100 -ConstraintsJson ""
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
  call :is_runserver_alive
  if not errorlevel 1 (
    echo [WARN] first client attempt failed, retry once...
    powershell -NoProfile -ExecutionPolicy Bypass -File "%CLIENT_PS1%" -HostName "!HOST!" -Port !PORT! -Prompt "tpose" -Duration 5.0 -Seed 42 -DiffusionSteps 100 -ConstraintsJson ""
    set "RC=%ERRORLEVEL%"
  )
)
echo [TEST] client exit code: %RC%

call :quit_and_wait
if not "%RC%"=="0" exit /b %RC%
echo [OK] example_run_server_tpose passed.
exit /b 0

:quit_and_wait
if exist "!PORT_FILE!" (
  call :read_serverport_retry
  set "QHOST=!HOST!"
  set "QPORT=!PORT!"
  if defined QHOST if defined QPORT (
    powershell -NoProfile -ExecutionPolicy Bypass -Command ^
      "$ErrorActionPreference='SilentlyContinue'; $h='%QHOST%'; $p=[int]%QPORT%; $c=New-Object Net.Sockets.TcpClient; $iar=$c.BeginConnect($h,$p,$null,$null); if($iar.AsyncWaitHandle.WaitOne(1500)){ $c.EndConnect($iar); $s=$c.GetStream(); $w=New-Object IO.StreamWriter($s); $w.AutoFlush=$true; $w.WriteLine('{""cmd"":""quit""}'); $w.Close(); $s.Close() }; $c.Close();" >nul 2>nul
  )
)
call :wait_pid_exit
exit /b 0

:wait_pid_exit
if not exist "!PID_FILE!" exit /b 0
set "SPID="
for /f "usebackq delims=" %%A in ("!PID_FILE!") do (
  if not defined SPID set "SPID=%%A"
)
if not defined SPID (
  call :archive_file "!PID_FILE!"
  exit /b 0
)
set /a WAIT_SEC=0
:wait_loop
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$pidValue='%SPID%'; if($pidValue -notmatch '^\d+$'){ exit 0 }; $p=Get-Process -Id ([int]$pidValue) -ErrorAction SilentlyContinue; if($null -eq $p){ exit 0 } else { exit 1 }" >nul 2>nul
if not errorlevel 1 (
  call :archive_file "%PID_FILE%"
  exit /b 0
)
ping 127.0.0.1 -n 2 >nul
set /a WAIT_SEC+=1
if !WAIT_SEC! geq 15 (
  echo [WARN] run_server wrapper still alive after quit grace; watchdog is expected to finish bridge cleanup.
  call :archive_file "!PID_FILE!"
  exit /b 0
)
goto wait_loop

:archive_file
set "ARCHIVE_TARGET=%~1"
if not exist "%ARCHIVE_TARGET%" exit /b 0
set "TS=%DATE:~0,4%%DATE:~5,2%%DATE:~8,2%_%TIME:~0,2%%TIME:~3,2%%TIME:~6,2%"
set "TS=%TS: =0%"
set "BASE=%~nx1"
set "DEST=%RECYCLE_DIR%\%BASE%.%TS%.%RANDOM%"
set /a ARCH_TRY=0
:archive_retry
move "%ARCHIVE_TARGET%" "%DEST%" >nul 2>nul
if not errorlevel 1 exit /b 0
if not exist "%ARCHIVE_TARGET%" exit /b 0
set /a ARCH_TRY+=1
if !ARCH_TRY! geq 5 (
  echo [WARN] archive skip ^(file busy^): %ARCHIVE_TARGET%
  exit /b 0
)
ping 127.0.0.1 -n 2 >nul
goto archive_retry
exit /b 0

:read_serverport_retry
set "HOST="
set "PORT="
for /l %%R in (1,1,40) do (
  if not exist "!PORT_FILE!" exit /b 1
  set "HOST="
  set "PORT="
  for /f "usebackq tokens=1,2 delims=:" %%A in ("!PORT_FILE!") do (
    set "HOST=%%A"
    set "PORT=%%B"
  )
  if defined HOST if defined PORT exit /b 0
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Sleep -Milliseconds 150" >nul 2>nul
)
exit /b 1

:read_endpoint_from_logs
set "HOST="
set "PORT="
for /f "usebackq tokens=1,2 delims=:" %%A in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='SilentlyContinue'; $p='%BRIDGE_SERVER_LOG%'; $h=''; $pt=''; if(Test-Path -LiteralPath $p){ $lines=Get-Content -LiteralPath $p -Tail 240; for($i=$lines.Count-1; $i -ge 0; $i--){ $line=[string]$lines[$i]; if($line -match 'ready host=(?<host>\\S+) port=(?<port>\\d+)'){ $h=$Matches['host']; $pt=$Matches['port']; break } } }; if($h -and $pt){ Write-Output ($h + ':' + $pt) }"`) do (
  if not defined HOST set "HOST=%%A"
  if not defined PORT set "PORT=%%B"
)
if defined HOST if defined PORT (
  echo [TEST] endpoint from bridge log: !HOST!:!PORT!
  exit /b 0
)
exit /b 1

:is_runserver_alive
if not exist "!PID_FILE!" exit /b 1
set "RPID="
for /f "usebackq delims=" %%A in ("!PID_FILE!") do (
  if not defined RPID set "RPID=%%A"
)
if not defined RPID exit /b 1
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$pidValue='%RPID%'; if($pidValue -notmatch '^\d+$'){ exit 1 }; $p=Get-Process -Id ([int]$pidValue) -ErrorAction SilentlyContinue; if($null -eq $p){ exit 1 } else { exit 0 }" >nul 2>nul
if errorlevel 1 exit /b 1
exit /b 0

:dump_startup_logs
echo [DIAG] tail: %ROOT_DIR%\log\setup.log
powershell -NoProfile -ExecutionPolicy Bypass -Command "if(Test-Path '%ROOT_DIR%\log\setup.log'){Get-Content '%ROOT_DIR%\log\setup.log' -Tail 40}" 2>nul
echo [DIAG] tail: %ROOT_DIR%\log\bridge_server.log
powershell -NoProfile -ExecutionPolicy Bypass -Command "if(Test-Path '%ROOT_DIR%\log\bridge_server.log'){Get-Content '%ROOT_DIR%\log\bridge_server.log' -Tail 40}" 2>nul
echo [DIAG] tail: %ROOT_DIR%\log\watchdog.log
powershell -NoProfile -ExecutionPolicy Bypass -Command "if(Test-Path '%ROOT_DIR%\log\watchdog.log'){Get-Content '%ROOT_DIR%\log\watchdog.log' -Tail 40}" 2>nul
exit /b 0
