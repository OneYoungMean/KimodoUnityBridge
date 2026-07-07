@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
set "ROOT_DIR=%SCRIPT_DIR%"
set "SOURCE_ROOT=%ROOT_DIR%\kimodo"
if not exist "%SOURCE_ROOT%\pyproject.toml" set "SOURCE_ROOT=%ROOT_DIR%"
set "BOOTSTRAP_LOCK=%ROOT_DIR%\.bootstrap.lock"
set "UV_TOOL_DIR=%ROOT_DIR%\program\exe\uv"
set "UV_BIN="
set "UV_INSTALL_TIMEOUT_SEC=600"
set "UV_PROBE_TIMEOUT_SEC=1"
set "UV_VERSION=0.11.25"
set "UV_ARTIFACT=uv-x86_64-pc-windows-msvc.zip"
set "UV_GITHUB_URL=https://github.com/astral-sh/uv/releases/download/%UV_VERSION%/%UV_ARTIFACT%"
set "UV_USTC_URL=https://mirrors.ustc.edu.cn/github-release/astral-sh/uv/%UV_VERSION%/%UV_ARTIFACT%"
set "UV_AUTO_INSTALL="
if defined KIMODO_AUTO_INSTALL_UV set "UV_AUTO_INSTALL=%KIMODO_AUTO_INSTALL_UV%"
set "SETUP_ARGS=setup --output file"
set "CLI_ARGS=run --output file"
set "EXPLICIT_VENV=%KIMODO_VENV_PATH%"
set "HOLD_CLI="

call :acquire_bootstrap_lock
if errorlevel 1 exit /b 1
call :resolve_uv_bin
if not defined UV_BIN (
  call :prompt_install_uv
  if errorlevel 1 goto cleanup_fail
  call :resolve_uv_bin
)
if not defined UV_BIN (
  echo [ERROR] uv is still unavailable after the download attempt.
  goto cleanup_fail
)

:parse_args
if "%~1"=="" goto after_parse
if /I "%~1"=="--force-setup" (
  set "SETUP_ARGS=%SETUP_ARGS% --force-setup"
  set "CLI_ARGS=%CLI_ARGS% --force-setup"
  shift
  goto parse_args
)
if /I "%~1"=="--hold-cli" (
  set "HOLD_CLI=1"
  shift
  goto parse_args
)
if /I "%~1"=="--watchpid" (
  if "%~2"=="" (
    echo [ERROR] --watchpid requires a value.
    goto cleanup_fail
  )
  set "CLI_ARGS=%CLI_ARGS% --watchpid %~2"
  shift
  shift
  goto parse_args
)
if /I "%~1"=="--venv" (
  if "%~2"=="" (
    echo [ERROR] --venv requires a path.
    goto cleanup_fail
  )
  set "EXPLICIT_VENV=%~2"
  set "SETUP_ARGS=%SETUP_ARGS% --venv ""%~2"""
  shift
  shift
  goto parse_args
)
shift
goto parse_args

:after_parse
"%UV_BIN%" run --python 3.12 --no-project python "%ROOT_DIR%\quickserver.py" %SETUP_ARGS%
if errorlevel 1 goto cleanup_fail

call :resolve_venv_python
if not defined VENV_PYTHON (
  echo [ERROR] Failed to resolve QuickServer venv python.
  goto cleanup_fail
)

call :release_bootstrap_lock
set "PYTHONPATH=%SOURCE_ROOT%"
if not exist "%ROOT_DIR%\log" mkdir "%ROOT_DIR%\log" >nul 2>nul
> "%ROOT_DIR%\log\run_server_cli_launch.log" (
  echo VENV_PYTHON=%VENV_PYTHON%
  echo SOURCE_ROOT=%SOURCE_ROOT%
  echo CLI_ARGS=%CLI_ARGS%
)
if defined HOLD_CLI (
  echo [INFO] Holding batch until quickserver_cli exits...
)
"%VENV_PYTHON%" -m kimodo.bridge.quickserver_cli %CLI_ARGS%
>> "%ROOT_DIR%\log\run_server_cli_launch.log" echo CLI_RC=%ERRORLEVEL%
exit /b %ERRORLEVEL%

:cleanup_fail
call :release_bootstrap_lock
exit /b 1

:acquire_bootstrap_lock
set "BOOTSTRAP_PID="
for /f "usebackq delims=" %%I in (`powershell -NoProfile -Command "[System.Diagnostics.Process]::GetCurrentProcess().Id"`) do set "BOOTSTRAP_PID=%%I"
if not defined BOOTSTRAP_PID exit /b 1

:lock_wait
if exist "%BOOTSTRAP_LOCK%" (
  set "LOCK_OWNER="
  for /f "usebackq tokens=1,* delims==" %%A in ("%BOOTSTRAP_LOCK%") do (
    if /I "%%A"=="owner_pid" set "LOCK_OWNER=%%B"
  )
  if defined LOCK_OWNER (
    tasklist /FI "PID eq !LOCK_OWNER!" 2>nul | findstr /R /C:"[ ]!LOCK_OWNER![ ]" >nul
    if not errorlevel 1 (
      if exist "%ROOT_DIR%\serverport" goto :eof
      timeout /t 1 /nobreak >nul
      goto lock_wait
    )
  )
  del /f /q "%BOOTSTRAP_LOCK%" >nul 2>nul
  if exist "%BOOTSTRAP_LOCK%" (
    timeout /t 1 /nobreak >nul
    goto lock_wait
  )
)

powershell -NoProfile -Command "$p='%BOOTSTRAP_LOCK%';$dir=[System.IO.Path]::GetDirectoryName($p);if($dir){[System.IO.Directory]::CreateDirectory($dir)|Out-Null};$fs=[System.IO.File]::Open($p,[System.IO.FileMode]::CreateNew,[System.IO.FileAccess]::Write,[System.IO.FileShare]::None);$sw=New-Object System.IO.StreamWriter($fs,[System.Text.UTF8Encoding]::new($false));$sw.WriteLine('owner_pid=%BOOTSTRAP_PID%');$sw.WriteLine('started_epoch=' + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds());$sw.Dispose()" >nul 2>nul
if errorlevel 1 (
  timeout /t 1 /nobreak >nul
  goto lock_wait
)
exit /b 0

:release_bootstrap_lock
if exist "%BOOTSTRAP_LOCK%" del /f /q "%BOOTSTRAP_LOCK%" >nul 2>nul
exit /b 0

:resolve_venv_python
set "VENV_PYTHON="
if defined EXPLICIT_VENV (
  if exist "%EXPLICIT_VENV%\Scripts\python.exe" (
    set "VENV_PYTHON=%EXPLICIT_VENV%\Scripts\python.exe"
    exit /b 0
  )
  if exist "%EXPLICIT_VENV%" (
    set "VENV_PYTHON=%EXPLICIT_VENV%"
    exit /b 0
  )
)
if exist "%SOURCE_ROOT%\.venv\Scripts\python.exe" (
  set "VENV_PYTHON=%SOURCE_ROOT%\.venv\Scripts\python.exe"
)
exit /b 0

:resolve_uv_bin
set "UV_BIN="
if defined KIMODO_UV_BIN (
  call :check_uv_candidate "%KIMODO_UV_BIN%"
  if defined UV_BIN goto :eof
)
call :check_uv_candidate "%UV_TOOL_DIR%\uv.exe"
if defined UV_BIN goto :eof
for /f "delims=" %%I in ('where uv.exe 2^>nul') do (
  call :check_uv_candidate "%%~fI"
  if defined UV_BIN goto :eof
)
goto :eof

:check_uv_candidate
set "UV_CANDIDATE=%~1"
if not defined UV_CANDIDATE goto :eof
if not exist "%UV_CANDIDATE%" goto :eof
"%UV_CANDIDATE%" --version >nul 2>nul
if errorlevel 1 goto :eof
set "UV_BIN=%UV_CANDIDATE%"
goto :eof

:prompt_install_uv
set "UV_ANSWER="
echo [ERROR] uv is required but was not found.
echo         QuickServer can download it into: %UV_TOOL_DIR%
if defined UV_AUTO_INSTALL goto install_uv
set /p UV_ANSWER=Would you like QuickServer to download uv now? [Y/N] 
if /I "%UV_ANSWER%"=="Y" goto install_uv
if /I "%UV_ANSWER%"=="YES" goto install_uv
if /I "%UV_ANSWER%"=="N" exit /b 1
if /I "%UV_ANSWER%"=="NO" exit /b 1
exit /b 1

:install_uv
if not exist "%UV_TOOL_DIR%" mkdir "%UV_TOOL_DIR%" >nul 2>nul
echo [INFO] Probing uv download sources for this launch...
echo [INFO] Download uv...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';$installDir='%UV_TOOL_DIR%';$artifact='%UV_ARTIFACT%';$probeTimeout=%UV_PROBE_TIMEOUT_SEC%;$downloadTimeout=%UV_INSTALL_TIMEOUT_SEC%;$candidates=@(@{Name='github';Url='%UV_GITHUB_URL%'},@{Name='ustc';Url='%UV_USTC_URL%'});function Probe([string]$name,[string]$url){$result=& curl.exe -I -L -o NUL -s -w '%%{http_code} %%{time_total}' --max-time $probeTimeout $url; if($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($result)){Write-Host ('[PROBE] uv {0}: failed, timeout={1}s, {2}' -f $name,$probeTimeout,$url); return $null}; $parts=$result.Trim().Split(' '); if($parts.Length -lt 2){Write-Host ('[PROBE] uv {0}: failed, malformed response, {1}' -f $name,$url); return $null}; $status=[int]$parts[0]; $seconds=0.0; [double]::TryParse($parts[1],[ref]$seconds) | Out-Null; $ms=[int][Math]::Round($seconds*1000); if($status -ge 200 -and $status -lt 400){Write-Host ('[PROBE] uv {0}: ok, {1} ms, {2}' -f $name,$ms,$url); return [pscustomobject]@{Name=$name;Url=$url;Ms=$ms}}; Write-Host ('[PROBE] uv {0}: failed, status={1}, {2}' -f $name,$status,$url); return $null}; $probed=@(); foreach($c in $candidates){$r=Probe $c.Name $c.Url; if($null -ne $r){$probed+=$r}}; if($probed.Count -eq 0){throw 'Unable to reach any uv download source for this launch.'}; $selected=$probed | Sort-Object Ms | Select-Object -First 1; Write-Host ('[INFO] Selected uv source: {0}' -f $selected.Name); $tempRoot=Join-Path ([IO.Path]::GetTempPath()) ('kimodo-uv-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null; try { $archivePath=Join-Path $tempRoot $artifact; & curl.exe -L --fail --silent --show-error --max-time $downloadTimeout -o $archivePath $selected.Url; if($LASTEXITCODE -ne 0){throw 'curl download failed.'}; Expand-Archive -LiteralPath $archivePath -DestinationPath $tempRoot -Force; New-Item -ItemType Directory -Force -Path $installDir | Out-Null; foreach($name in @('uv.exe','uvx.exe','uvw.exe')){ $source=Join-Path $tempRoot $name; if(Test-Path -LiteralPath $source){ Copy-Item -LiteralPath $source -Destination (Join-Path $installDir $name) -Force } }; Write-Host '[INFO] Download uv complete.' } finally { if(Test-Path -LiteralPath $tempRoot){ Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue } }"
if errorlevel 1 exit /b 1
exit /b 0
