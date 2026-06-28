@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
for %%I in ("%SCRIPT_DIR%\..") do set "ROOT_DIR=%%~fI"
set "LOG_DIR=%ROOT_DIR%\log"
set "ARCHIVE_DIR=%ROOT_DIR%\archive\recycle"
set "PORT_FILE=%ROOT_DIR%\serverport"
set "PID_FILE=%LOG_DIR%\test_multi_start_server.pid"
set "BRIDGE_LOG=%LOG_DIR%\test_multi_start_bridge.log"
set "BOOTSTRAP_LOG=%LOG_DIR%\bridge_bootstrap_error.log"
set "BRIDGE_FIXED_LOG=%LOG_DIR%\bridge_server.log"
set "MODELS_ROOT=C:\nvlab\models~"
if defined KIMODO_TEST_MODELS_ROOT set "MODELS_ROOT=%KIMODO_TEST_MODELS_ROOT%"

if not exist "%ROOT_DIR%\run_server.bat" (
  echo [ERROR] run_server.bat not found: %ROOT_DIR%\run_server.bat
  exit /b 1
)
if not exist "%LOG_DIR%" mkdir "%LOG_DIR%" >nul 2>nul
if not exist "%ARCHIVE_DIR%" mkdir "%ARCHIVE_DIR%" >nul 2>nul
if not exist "%MODELS_ROOT%" (
  echo [ERROR] models root not found: %MODELS_ROOT%
  exit /b 1
)

call :archive_file "%PID_FILE%"
call :archive_file "%BRIDGE_LOG%"
call :archive_file "%BOOTSTRAP_LOG%"
call :archive_file "%PORT_FILE%"
set "RUN_WRAPPER=%LOG_DIR%\test_multi_start_wrapper.bat"
set "RUN_WINDOW_TITLE=KIMODO_TEST_MULTI_START"

echo [TEST] ROOT_DIR=%ROOT_DIR%
echo [TEST] MODELS_ROOT=%MODELS_ROOT%
echo [TEST] VENV_PATH=^<disabled^>

call :start_server_background
if errorlevel 1 exit /b 1

set "PORT_BASE="
set /a WAIT_SEC=0
:wait_port
if exist "%PORT_FILE%" (
  for /f "usebackq tokens=1,2 delims=:" %%A in ("%PORT_FILE%") do (
    if not defined PORT_BASE set "PORT_BASE=%%A:%%B"
  )
)
if defined PORT_BASE goto got_port
ping 127.0.0.1 -n 2 >nul
set /a WAIT_SEC+=1
if !WAIT_SEC! geq 120 (
  echo [ERROR] timeout waiting serverport.
  call :archive_file "%PID_FILE%"
  exit /b 1
)
goto wait_port

:got_port
echo [TEST] initial serverport=%PORT_BASE%
call :wait_bridge_ready_log 180
if errorlevel 1 (
  echo [ERROR] initial server did not reach ready state.
  call :archive_file "%PID_FILE%"
  exit /b 1
)

for /l %%I in (1,1,3) do (
  call :simulate_user_start "%%I"
  if errorlevel 1 (
    call :quit_server
    call :archive_file "%PID_FILE%"
    exit /b 1
  )
)

echo [OK] multi-start simulation passed.
call :quit_server
call :archive_file "%PID_FILE%"
exit /b 0

:simulate_user_start
set "RUN_IDX=%~1"
set "RUN_LOG=%LOG_DIR%\test_multi_start_run%RUN_IDX%.log"
call :archive_file "%RUN_LOG%"

pushd "%ROOT_DIR%" >nul
	for /f %%P in ('powershell -NoProfile -ExecutionPolicy Bypass -Command "$ownerPidValue=(Get-CimInstance Win32_Process -Filter ('ProcessId=' + $PID) | Select-Object -ExpandProperty ParentProcessId); if(-not $ownerPidValue){$ownerPidValue=$PID}; Write-Output $ownerPidValue"') do set "SIM_OWNER_PID=%%P"
call run_server.bat --model Kimodo-SOMA-RP-v1 --models-root "%MODELS_ROOT%" --watchpid "!SIM_OWNER_PID!" --output file --log "%RUN_LOG%"
set "RC=%ERRORLEVEL%"
popd >nul
if not "%RC%"=="0" (
  echo [ERROR] repeated start #%RUN_IDX% returned %RC%.
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Content -LiteralPath '%RUN_LOG%' -Tail 60"
  exit /b 1
)

findstr /C:"Existing server already running with same params" "%RUN_LOG%" >nul
if errorlevel 1 (
  findstr /C:"Existing server signature matches, but probe failed. Restarting..." "%RUN_LOG%" >nul
  if errorlevel 1 (
    echo [ERROR] repeated start #%RUN_IDX% neither fast-path nor controlled-restart.
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Content -LiteralPath '%RUN_LOG%' -Tail 120"
    exit /b 1
  )
)

set "PORT_NOW="
if exist "%PORT_FILE%" (
  for /f "usebackq tokens=1,2 delims=:" %%A in ("%PORT_FILE%") do (
    if not defined PORT_NOW set "PORT_NOW=%%A:%%B"
  )
)
if not defined PORT_NOW (
  echo [ERROR] repeated start #%RUN_IDX% lost serverport.
  exit /b 1
)
if /I not "%PORT_NOW%"=="%PORT_BASE%" (
  echo [WARN] repeated start #%RUN_IDX% changed serverport: %PORT_BASE% -> %PORT_NOW%
  set "PORT_BASE=%PORT_NOW%"
)
call :wait_server_responding 120
if errorlevel 1 (
  echo [ERROR] repeated start #%RUN_IDX% server not responsive after start.
  exit /b 1
)
call :assert_single_bridge_process
if errorlevel 1 (
  echo [ERROR] repeated start #%RUN_IDX% found multiple bridge processes.
  exit /b 1
)
echo [CASE] repeated start #%RUN_IDX% pass.
exit /b 0

:start_server_background
> "%RUN_WRAPPER%" (
  echo @echo off
  echo cd /d "%ROOT_DIR%"
	  echo for /f %%%%P in ^('powershell -NoProfile -ExecutionPolicy Bypass -Command "$ownerPidValue=(Get-CimInstance Win32_Process -Filter ('ProcessId=' + $PID) ^| Select-Object -ExpandProperty ParentProcessId); if(-not $ownerPidValue){$ownerPidValue=$PID}; Write-Output $ownerPidValue"'^) do set "OWNER_PID=%%%%P"
  echo call run_server.bat --model Kimodo-SOMA-RP-v1 --models-root "%MODELS_ROOT%" --watchpid %%OWNER_PID%% --output file --log "%BRIDGE_LOG%"
)
start "%RUN_WINDOW_TITLE%" cmd /k call "%RUN_WRAPPER%"
exit /b 0

:wait_server_responding
set /a RESP_WAIT_MAX=%~1
if not defined RESP_WAIT_MAX set /a RESP_WAIT_MAX=120
set /a RESP_WAIT_CUR=0
:wait_resp_loop
if not exist "%PORT_FILE%" goto resp_sleep
set "RHOST="
set "RPORT="
for /f "usebackq tokens=1,2 delims=:" %%A in ("%PORT_FILE%") do (
  set "RHOST=%%A"
  set "RPORT=%%B"
)
if defined RHOST if defined RPORT (
  powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$ErrorActionPreference='SilentlyContinue'; $h='%RHOST%'; $p=[int]%RPORT%; $c=$null; $s=$null; $w=$null; $r=$null; try { $c=New-Object Net.Sockets.TcpClient; $iar=$c.BeginConnect($h,$p,$null,$null); if(-not $iar.AsyncWaitHandle.WaitOne(1200)){ exit 1 }; $c.EndConnect($iar); $s=$c.GetStream(); $w=New-Object IO.StreamWriter($s); $r=New-Object IO.StreamReader($s); $w.AutoFlush=$true; $w.WriteLine('{""cmd"":""ping""}'); $line=$r.ReadLine(); if([string]::IsNullOrWhiteSpace($line)){ exit 1 } else { exit 0 } } catch { exit 1 } finally { if($r){$r.Close()}; if($w){$w.Close()}; if($s){$s.Close()}; if($c){$c.Close()} }" >nul 2>nul
  if not errorlevel 1 exit /b 0
)
:resp_sleep
ping 127.0.0.1 -n 2 >nul
set /a RESP_WAIT_CUR+=1
if !RESP_WAIT_CUR! geq !RESP_WAIT_MAX! exit /b 1
goto wait_resp_loop

:wait_bridge_ready_log
set /a READY_WAIT_MAX=%~1
if not defined READY_WAIT_MAX set /a READY_WAIT_MAX=180
set /a READY_WAIT_CUR=0
:wait_ready_loop
if exist "%BRIDGE_LOG%" (
  findstr /C:"[bridge] ready host=" "%BRIDGE_LOG%" >nul
  if not errorlevel 1 exit /b 0
)
if exist "%BOOTSTRAP_LOG%" (
  findstr /C:"[bridge] ready host=" "%BOOTSTRAP_LOG%" >nul
  if not errorlevel 1 exit /b 0
)
if exist "%BRIDGE_FIXED_LOG%" (
  findstr /C:"[bridge] ready host=" "%BRIDGE_FIXED_LOG%" >nul
  if not errorlevel 1 exit /b 0
)
ping 127.0.0.1 -n 2 >nul
set /a READY_WAIT_CUR+=1
if !READY_WAIT_CUR! geq !READY_WAIT_MAX! exit /b 1
goto wait_ready_loop

:assert_single_bridge_process
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop'; $root=(Resolve-Path -LiteralPath '%ROOT_DIR%').Path; $rootEsc=[regex]::Escape($root); $pat='--kimodo-root\\s+\"?' + $rootEsc + '\"?(\\s|$)'; for($i=0;$i -lt 10;$i++){ $ps=Get-CimInstance Win32_Process -Filter \"Name='python.exe'\" | Where-Object { $_.CommandLine -and $_.CommandLine -match 'kimodo\\.bridge\\.bridge_server' -and $_.CommandLine -match $pat }; $cnt=($ps | Measure-Object).Count; if($cnt -le 1){ exit 0 }; Start-Sleep -Seconds 1 }; exit 1" >nul 2>nul
if errorlevel 1 exit /b 1
exit /b 0

:quit_server
if not exist "%PORT_FILE%" exit /b 0
set "QHOST="
set "QPORT="
for /f "usebackq tokens=1,2 delims=:" %%A in ("%PORT_FILE%") do (
  set "QHOST=%%A"
  set "QPORT=%%B"
)
if not defined QHOST exit /b 0
if not defined QPORT exit /b 0
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='SilentlyContinue'; $h='%QHOST%'; $p=[int]%QPORT%; $c=New-Object Net.Sockets.TcpClient($h,$p); $s=$c.GetStream(); $w=New-Object IO.StreamWriter($s); $w.AutoFlush=$true; $w.WriteLine('{""cmd"":""quit""}'); $w.Close(); $s.Close(); $c.Close();" >nul 2>nul
exit /b 0

:_wait_exit
set /a WAIT_MAX=%~1
if not defined WAIT_MAX set /a WAIT_MAX=30
if not exist "%PID_FILE%" exit /b 0
set "SPID="
for /f "usebackq delims=" %%A in ("%PID_FILE%") do (
  if not defined SPID set "SPID=%%A"
)
if not defined SPID exit /b 0
set /a WAIT_CUR=0
:wait_exit_loop
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$pidValue='%SPID%'; if($pidValue -notmatch '^\d+$'){ exit 0 }; $p=Get-Process -Id ([int]$pidValue) -ErrorAction SilentlyContinue; if($null -eq $p){ exit 0 } else { exit 1 }" >nul 2>nul
if not errorlevel 1 (
  call :archive_file "%PID_FILE%"
  exit /b 0
)
ping 127.0.0.1 -n 2 >nul
set /a WAIT_CUR+=1
if !WAIT_CUR! geq !WAIT_MAX! (
  echo [WARN] server wrapper still alive after wait; relying on watchdog cleanup.
  call :archive_file "%PID_FILE%"
  exit /b 0
)
goto wait_exit_loop

:archive_file
set "ARCHIVE_TARGET=%~1"
if not exist "%ARCHIVE_TARGET%" exit /b 0
set "TS=%DATE:~0,4%%DATE:~5,2%%DATE:~8,2%_%TIME:~0,2%%TIME:~3,2%%TIME:~6,2%"
set "TS=%TS: =0%"
set "BASE=%~nx1"
set "DEST=%ARCHIVE_DIR%\%BASE%.%TS%.%RANDOM%"
move "%ARCHIVE_TARGET%" "%DEST%" >nul 2>nul
exit /b 0
