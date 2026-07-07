@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
set "ROOT_DIR=%SCRIPT_DIR%"
set "UV_TOOL_DIR=%ROOT_DIR%\program\exe\uv"
set "UV_BIN="
set "UV_INSTALL_TIMEOUT_SEC=600"
set "UV_PROBE_TIMEOUT_SEC=1"
set "UV_VERSION=0.11.25"
set "UV_ARTIFACT=uv-x86_64-pc-windows-msvc.zip"
set "UV_GITHUB_URL=https://github.com/astral-sh/uv/releases/download/%UV_VERSION%/%UV_ARTIFACT%"
set "UV_USTC_URL=https://mirrors.ustc.edu.cn/github-release/astral-sh/uv/%UV_VERSION%/%UV_ARTIFACT%"
set "UV_AUTO_INSTALL="
set "RAW_ARGS=%*"
if /I not "%RAW_ARGS:--output file=%"=="%RAW_ARGS%" set "UV_AUTO_INSTALL=1"
if /I not "%RAW_ARGS:--output \"file\"=%"=="%RAW_ARGS%" set "UV_AUTO_INSTALL=1"
if defined KIMODO_AUTO_INSTALL_UV set "UV_AUTO_INSTALL=%KIMODO_AUTO_INSTALL_UV%"
if defined KIMODO_TEST_VENV_PATH (
  echo [ERROR] KIMODO_TEST_VENV_PATH has been removed. Use KIMODO_VENV_PATH.
  exit /b 1
)
if defined KIMODO_TEST_SETUP_DEVICE (
  echo [ERROR] KIMODO_TEST_SETUP_DEVICE has been removed. Use KIMODO_SETUP_DEVICE.
  exit /b 1
)
if defined KIMODO_CPU_TEXT_ENCODER (
  echo [ERROR] KIMODO_CPU_TEXT_ENCODER has been removed. QuickServer now auto-selects the local INT8 text encoder route.
  exit /b 1
)
if defined CHECKPOINT_DIR (
  echo [ERROR] CHECKPOINT_DIR has been removed. Use KIMODO_MODELS_ROOT.
  exit /b 1
)
set "VENV_OVERRIDE=%KIMODO_VENV_PATH%"

call :resolve_uv_bin
if not defined UV_BIN (
  call :prompt_install_uv || exit /b 1
  call :resolve_uv_bin
)
if not defined UV_BIN (
  echo [ERROR] uv is still unavailable after the download attempt.
  exit /b 1
)

set "ARGS="
set "HAS_VENV_ARG="
:collect_args
if "%~1"=="" goto launch
set "NEXT=%~1"
if /I "%NEXT%"=="--venv" set "HAS_VENV_ARG=1"
if defined ARGS (
  set "ARGS=!ARGS! "!NEXT!""
) else (
  set "ARGS="!NEXT!""
)
shift
goto collect_args

:launch
if defined VENV_OVERRIDE if not defined HAS_VENV_ARG (
  if defined ARGS (
    set "ARGS=!ARGS! "--venv" "%VENV_OVERRIDE%""
  ) else (
    set "ARGS="--venv" "%VENV_OVERRIDE%""
  )
)
"%UV_BIN%" run --python 3.12 --no-project python "%ROOT_DIR%\quickserver.py" !ARGS!
exit /b %ERRORLEVEL%

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
if errorlevel 1 (
  echo [ERROR] Failed to download uv automatically within 10 minutes.
  echo [ERROR] Please install uv manually, or place uv under: %UV_TOOL_DIR%
  exit /b 1
)
exit /b 0
