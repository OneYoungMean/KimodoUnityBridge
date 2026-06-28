@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
set "ROOT_DIR=%SCRIPT_DIR%\.."
set "LOG_DIR=%ROOT_DIR%\log"
set "ARCHIVE_DIR=%ROOT_DIR%\archive\recycle"
set "PORT_FILE=%ROOT_DIR%\serverport"
set "MODELS_ROOT=C:\nvlab\models~"

if not exist "%ROOT_DIR%\run_server.bat" (
  echo [ERROR] run_server.bat not found under: %ROOT_DIR%
  exit /b 1
)
if not exist "%LOG_DIR%" mkdir "%LOG_DIR%" >nul 2>nul
if not exist "%ARCHIVE_DIR%" mkdir "%ARCHIVE_DIR%" >nul 2>nul

call :archive_file "%PORT_FILE%"
call :cleanup_bridge_processes

set "FAIL_COUNT=0"
set "LAUNCH_TITLE=KIMODO_TEST_WATCHDOG"
call :run_case "defaults" "" "" "" ""
call :run_case "custom_fast" "2" "12" "" ""
call :run_case "custom_runtime_idle" "1" "30" "2" "15"

if not "%FAIL_COUNT%"=="0" (
  echo [RESULT] FAILED cases=%FAIL_COUNT%
  exit /b 1
)
echo [RESULT] ALL PASSED
exit /b 0

:run_case
set "CASE_NAME=%~1"
set "CASE_STARTUP_INTERVAL=%~2"
set "CASE_MAX_FAILS=%~3"
set "CASE_RUNTIME_INTERVAL=%~4"
set "CASE_IDLE_NOLOG_MAX=%~5"
set "CASE_OWNER_PID=%~6"

echo [CASE] %CASE_NAME%

set "CASE_RUN_LOG=%LOG_DIR%\test_watchdog_%CASE_NAME%_run.log"
set "CASE_BRIDGE_LOG=%LOG_DIR%\test_watchdog_%CASE_NAME%_bridge.log"
set "CASE_PID_FILE=%LOG_DIR%\test_watchdog_%CASE_NAME%_run_server.pid"

call :archive_file "%CASE_RUN_LOG%"
call :archive_file "%CASE_BRIDGE_LOG%"
call :archive_file "%CASE_PID_FILE%"
call :archive_file "%PORT_FILE%"

set "CASE_WRAPPER=%LOG_DIR%\test_watchdog_%CASE_NAME%_wrapper.bat"
> "%CASE_WRAPPER%" (
  echo @echo off
  echo setlocal EnableExtensions EnableDelayedExpansion
  echo cd /d "%ROOT_DIR%"
  if defined CASE_STARTUP_INTERVAL echo set "KIMODO_WATCHDOG_STARTUP_INTERVAL_SEC=%CASE_STARTUP_INTERVAL%"
  if defined CASE_MAX_FAILS echo set "KIMODO_WATCHDOG_STARTUP_MAX_FAILS=%CASE_MAX_FAILS%"
  if defined CASE_RUNTIME_INTERVAL echo set "KIMODO_WATCHDOG_RUNTIME_INTERVAL_SEC=%CASE_RUNTIME_INTERVAL%"
  if defined CASE_IDLE_NOLOG_MAX echo set "KIMODO_WATCHDOG_IDLE_NOLOG_MAX=%CASE_IDLE_NOLOG_MAX%"
  if defined CASE_OWNER_PID (
    echo call run_server.bat --model Kimodo-SOMA-RP-v1 --models-root "%MODELS_ROOT%" --watchpid %CASE_OWNER_PID% --output file --log "%CASE_RUN_LOG%"
  ) else (
    echo call run_server.bat --model Kimodo-SOMA-RP-v1 --models-root "%MODELS_ROOT%" --output file --log "%CASE_RUN_LOG%"
  )
  echo endlocal
)
start "%LAUNCH_TITLE%_%CASE_NAME%" cmd /k call "%CASE_WRAPPER%"
if errorlevel 1 (
  echo [CASE:%CASE_NAME%] [ERROR] failed to start run_server
  set /a FAIL_COUNT+=1
  exit /b 0
)

set "HOST="
set "PORT="
set /a WAIT_SEC=0
:wait_port
if exist "%PORT_FILE%" (
  for /f "usebackq tokens=1,2 delims=:" %%A in ("%PORT_FILE%") do (
    set "HOST=%%A"
    set "PORT=%%B"
  )
)
if defined HOST if defined PORT goto got_port
ping 127.0.0.1 -n 2 >nul
set /a WAIT_SEC+=1
if !WAIT_SEC! geq 120 (
  echo [CASE:%CASE_NAME%] [ERROR] timeout waiting serverport
  call :kill_case_window "%LAUNCH_TITLE%_%CASE_NAME%"
  set /a FAIL_COUNT+=1
  exit /b 0
)
goto wait_port

:got_port
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='SilentlyContinue'; $h='%HOST%'; $p=[int]%PORT%; $c=$null; try { $c=New-Object Net.Sockets.TcpClient($h,$p); $s=$c.GetStream(); $w=New-Object IO.StreamWriter($s); $w.AutoFlush=$true; $w.WriteLine('{""cmd"":""quit""}'); $w.Close(); $s.Close() } finally { if($c){$c.Close()} }" >nul 2>nul

call :wait_case_exit "%CASE_PID_FILE%" 90
call :count_bridge_processes BRIDGE_LEFT
if not "%BRIDGE_LEFT%"=="0" (
  echo [CASE:%CASE_NAME%] [FAIL] leftover bridge processes=%BRIDGE_LEFT%
  call :cleanup_bridge_processes
  set /a FAIL_COUNT+=1
  exit /b 0
)

set "EXP_STARTUP_INTERVAL=%CASE_STARTUP_INTERVAL%"
if not defined EXP_STARTUP_INTERVAL set "EXP_STARTUP_INTERVAL=1"
set "EXP_STARTUP_MAX_FAILS=%CASE_MAX_FAILS%"
if not defined EXP_STARTUP_MAX_FAILS set "EXP_STARTUP_MAX_FAILS=180"
set "EXP_RUNTIME_INTERVAL=%CASE_RUNTIME_INTERVAL%"
if not defined EXP_RUNTIME_INTERVAL set "EXP_RUNTIME_INTERVAL=1"
set "EXP_IDLE_NOLOG_MAX=%CASE_IDLE_NOLOG_MAX%"
if not defined EXP_IDLE_NOLOG_MAX set "EXP_IDLE_NOLOG_MAX=300"

set "EXPECT_A=startup_interval=%EXP_STARTUP_INTERVAL%s"
set "EXPECT_B=startup_max_fails=%EXP_STARTUP_MAX_FAILS%"
set "EXPECT_C=runtime_interval=%EXP_RUNTIME_INTERVAL%s"
set "EXPECT_D=idle_nolog_max=%EXP_IDLE_NOLOG_MAX%"
set "EXPECT_OWNER=owner_pid="

findstr /C:"%EXPECT_A%" "%CASE_RUN_LOG%" >nul || goto case_failed
findstr /C:"%EXPECT_B%" "%CASE_RUN_LOG%" >nul || goto case_failed
findstr /C:"%EXPECT_C%" "%CASE_RUN_LOG%" >nul || goto case_failed
findstr /C:"%EXPECT_D%" "%CASE_RUN_LOG%" >nul || goto case_failed
findstr /C:"%EXPECT_OWNER%" "%CASE_RUN_LOG%" >nul || goto case_failed

echo [CASE:%CASE_NAME%] [PASS]
exit /b 0

:case_failed
echo [CASE:%CASE_NAME%] [FAIL] expected watchdog line not found
echo [CASE:%CASE_NAME%] expected: %EXPECT_A%
echo [CASE:%CASE_NAME%] expected: %EXPECT_B%
echo [CASE:%CASE_NAME%] expected: %EXPECT_C%
echo [CASE:%CASE_NAME%] expected: %EXPECT_D%
if exist "%CASE_RUN_LOG%" (
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Content -LiteralPath '%CASE_RUN_LOG%' -Tail 80"
)
call :kill_case_window "%LAUNCH_TITLE%_%CASE_NAME%"
set /a FAIL_COUNT+=1
exit /b 0

:wait_case_exit
set "WAIT_PID_FILE=%~1"
set /a WAIT_MAX=%~2
set /a WAIT_CUR=0
:wait_case_loop
if not exist "%PORT_FILE%" exit /b 0
ping 127.0.0.1 -n 2 >nul
set /a WAIT_CUR+=1
if !WAIT_CUR! geq !WAIT_MAX! (
  call :kill_case_window "%LAUNCH_TITLE%_%CASE_NAME%"
  exit /b 0
)
goto wait_case_loop

:kill_case_window
set "KILL_TITLE=%~1"
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='SilentlyContinue'; $t='%KILL_TITLE%'; Get-CimInstance Win32_Process -Filter \"Name='cmd.exe'\" | Where-Object { $_.CommandLine -and $_.CommandLine -match [regex]::Escape($t) } | ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue } catch {} }" >nul 2>nul
exit /b 0

:count_bridge_processes
set "BRIDGE_COUNT_OUTVAR=%~1"
set "BRIDGE_COUNT_VALUE="
for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='SilentlyContinue'; $root=[IO.Path]::GetFullPath('%ROOT_DIR%'); $n=(Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine -match 'kimodo\.bridge\.bridge_server' -and $_.CommandLine -like ('*'+$root+'*') }).Count; if($null -eq $n){$n=0}; Write-Output $n"`) do (
  if not defined BRIDGE_COUNT_VALUE set "BRIDGE_COUNT_VALUE=%%I"
)
if not defined BRIDGE_COUNT_VALUE set "BRIDGE_COUNT_VALUE=0"
set "%BRIDGE_COUNT_OUTVAR%=%BRIDGE_COUNT_VALUE%"
exit /b 0

:cleanup_bridge_processes
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='SilentlyContinue'; $root=[IO.Path]::GetFullPath('%ROOT_DIR%'); Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine -match 'kimodo\.bridge\.bridge_server' -and $_.CommandLine -like ('*'+$root+'*') } | ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue } catch {} }" >nul 2>nul
exit /b 0

:archive_file
set "ARCHIVE_TARGET=%~1"
if not exist "%ARCHIVE_TARGET%" exit /b 0
set "TS=%DATE:~0,4%%DATE:~5,2%%DATE:~8,2%_%TIME:~0,2%%TIME:~3,2%%TIME:~6,2%"
set "TS=%TS: =0%"
set "BASE=%~nx1"
set "DEST=%ARCHIVE_DIR%\%BASE%.%TS%.%RANDOM%"
move "%ARCHIVE_TARGET%" "%DEST%" >nul 2>nul
exit /b 0
