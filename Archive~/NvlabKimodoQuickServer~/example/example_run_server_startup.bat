@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%.") do set "SCRIPT_DIR=%%~fI"
if "!SCRIPT_DIR:~-1!"=="\" set "SCRIPT_DIR=!SCRIPT_DIR:~0,-1!"
for %%I in ("!SCRIPT_DIR!\..") do set "ROOT_DIR=%%~fI"
for %%I in ("!ROOT_DIR!\run_server.bat") do set "LAUNCHER=%%~fI"
set "PORT_FILE=!ROOT_DIR!\serverport"
set "BRIDGE_LOG=!ROOT_DIR!\log\bridge_server.log"
set "PID_FILE=!ROOT_DIR!\log\example_run_server_startup.pid"
set "STARTUP_TIMEOUT_SEC=1800"
set "EXIT_GRACE_SEC=15"

if not exist "!LAUNCHER!" (
  echo [ERROR] run_server.bat not found: !LAUNCHER!
  exit /b 1
)
if not exist "!ROOT_DIR!\log" mkdir "!ROOT_DIR!\log" >nul 2>nul

echo [EXAMPLE] ROOT_DIR=!ROOT_DIR!
echo [EXAMPLE] Launching QuickServer startup example...

if exist "!PORT_FILE!" del /f /q "!PORT_FILE!" >nul 2>nul
if exist "!PID_FILE!" del /f /q "!PID_FILE!" >nul 2>nul

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop'; $p=Start-Process -FilePath 'cmd.exe' -ArgumentList @('/d','/c','call','!LAUNCHER!') -WorkingDirectory '!ROOT_DIR!' -WindowStyle Normal -PassThru; Set-Content -LiteralPath '!PID_FILE!' -Value $p.Id -Encoding ASCII"
if errorlevel 1 (
  echo [ERROR] failed to launch run_server.bat
  exit /b 1
)

set /a WAIT_SEC=0
:wait_ready
call :read_serverport
if not errorlevel 1 goto ready
call :read_endpoint_from_log
if not errorlevel 1 goto ready

call :is_wrapper_alive
if errorlevel 1 (
  if not defined WRAPPER_EXITED (
    set "WRAPPER_EXITED=1"
    set /a WRAPPER_EXIT_AT=WAIT_SEC
  )
  set /a EXIT_WAIT=WAIT_SEC-WRAPPER_EXIT_AT
  if !EXIT_WAIT! geq %EXIT_GRACE_SEC% (
    echo [ERROR] run_server.bat exited before startup endpoint became available.
    call :dump_logs
    exit /b 1
  )
)

timeout /t 1 /nobreak >nul
set /a WAIT_SEC+=1
set /a WAIT_MOD=WAIT_SEC %% 10
if !WAIT_MOD! equ 0 (
  if defined WRAPPER_EXITED (
    echo [EXAMPLE] waiting startup endpoint... !WAIT_SEC!/%STARTUP_TIMEOUT_SEC%s ^(wrapper exited, waiting for bridge handoff^)
  ) else (
    echo [EXAMPLE] waiting startup endpoint... !WAIT_SEC!/%STARTUP_TIMEOUT_SEC%s
  )
)
if !WAIT_SEC! geq %STARTUP_TIMEOUT_SEC% (
  echo [ERROR] startup endpoint did not appear within %STARTUP_TIMEOUT_SEC%s.
  call :dump_logs
  exit /b 1
)
goto wait_ready

:ready
echo [OK] QuickServer startup ready: !HOST!:!PORT!
call :send_quit
call :wait_wrapper_exit
exit /b 0

:read_serverport
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

:read_endpoint_from_log
set "HOST="
set "PORT="
if not exist "!BRIDGE_LOG!" exit /b 1
set "ENDPOINT="
for /f "usebackq tokens=5" %%L in (`findstr /C:"quickserver_cli listening on" "!BRIDGE_LOG!"`) do (
  if not defined ENDPOINT set "ENDPOINT=%%L"
)
if defined ENDPOINT (
  for /f "tokens=1,2 delims=:" %%A in ("!ENDPOINT!") do (
    if not defined HOST set "HOST=%%A"
    if not defined PORT set "PORT=%%B"
  )
)
if defined HOST if defined PORT exit /b 0
exit /b 1

:is_wrapper_alive
if not exist "!PID_FILE!" exit /b 1
set "WRAPPER_PID="
for /f "usebackq delims=" %%A in ("!PID_FILE!") do (
  if not defined WRAPPER_PID set "WRAPPER_PID=%%A"
)
if not defined WRAPPER_PID exit /b 1
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$pidValue='!WRAPPER_PID!'; if($pidValue -notmatch '^\d+$'){ exit 1 }; $p=Get-Process -Id ([int]$pidValue) -ErrorAction SilentlyContinue; if($null -eq $p){ exit 1 } else { exit 0 }" >nul 2>nul
exit /b %ERRORLEVEL%

:send_quit
if not defined HOST exit /b 0
if not defined PORT exit /b 0
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop'; $client=New-Object Net.Sockets.TcpClient; $client.Connect('!HOST!',[int]!PORT!); $stream=$client.GetStream(); $writer=New-Object IO.StreamWriter($stream); $writer.AutoFlush=$true; $writer.WriteLine('{""cmd"":""quit""}'); $writer.Dispose(); $stream.Dispose(); $client.Dispose();" >nul 2>nul
exit /b 0

:wait_wrapper_exit
set /a WAIT_SEC=0
:wait_wrapper_loop
call :is_wrapper_alive
if errorlevel 1 exit /b 0
timeout /t 1 /nobreak >nul
set /a WAIT_SEC+=1
if !WAIT_SEC! geq 30 (
  echo [WARN] wrapper still running after quit request; exiting example anyway.
  exit /b 0
)
goto wait_wrapper_loop

:dump_logs
if exist "!BRIDGE_LOG!" (
  echo [DIAG] tail: !BRIDGE_LOG!
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Content -LiteralPath '!BRIDGE_LOG!' -Tail 40" 2>nul
)
if exist "!ROOT_DIR!\log\setup.log" (
  echo [DIAG] tail: !ROOT_DIR!\log\setup.log
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Content -LiteralPath '!ROOT_DIR!\log\setup.log' -Tail 40" 2>nul
)
exit /b 0
