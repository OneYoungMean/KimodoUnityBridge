@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
set "ROOT_DIR=%SCRIPT_DIR%"
set "UV_TOOL_DIR=%ROOT_DIR%\program\exe\uv"
set "UV_BIN="
set "UV_INSTALL_TIMEOUT_SEC=600"
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
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';$installDir='%UV_TOOL_DIR%';$timeoutSec=%UV_INSTALL_TIMEOUT_SEC%;$candidates=@(@{Name='official';ScriptUrl='https://astral.sh/uv/install.ps1';GithubBaseUrl=$null},@{Name='release-mirror';ScriptUrl='https://releases.astral.sh/github/uv/releases/latest/download/uv-installer.ps1';GithubBaseUrl='https://releases.astral.sh/github'});function Probe-Url([string]$u){$w=[Diagnostics.Stopwatch]::StartNew();try{$r=Invoke-WebRequest -Method Head -UseBasicParsing -Uri $u -TimeoutSec 3;$w.Stop();[pscustomobject]@{Ok=$true;Ms=[int]$w.ElapsedMilliseconds;Status=[int]$r.StatusCode;Error=''}}catch{$w.Stop();$s=0;try{$s=[int]$_.Exception.Response.StatusCode.value__}catch{};[pscustomobject]@{Ok=$false;Ms=[int]$w.ElapsedMilliseconds;Status=$s;Error=$_.Exception.Message}}};$probed=@();foreach($c in $candidates){$r=Probe-Url $c.ScriptUrl;$o=[pscustomobject]@{Name=$c.Name;ScriptUrl=$c.ScriptUrl;GithubBaseUrl=$c.GithubBaseUrl;Ok=$r.Ok;Ms=$r.Ms;Status=$r.Status;Error=$r.Error};$probed+=$o;if($o.Ok){Write-Host ('[PROBE] uv {0}: ok, {1} ms, {2}' -f $o.Name,$o.Ms,$o.ScriptUrl)}else{Write-Host ('[PROBE] uv {0}: failed, {1} ms, status={2}, {3}' -f $o.Name,$o.Ms,$o.Status,$o.Error)}};$selected=$probed | Where-Object {$_.Ok} | Sort-Object Ms | Select-Object -First 1;if($null -eq $selected){throw 'Unable to reach any uv installer source for this launch.'};Write-Host ('[INFO] Selected uv source: {0}' -f $selected.Name);$env:UV_UNMANAGED_INSTALL=$installDir;if($selected.GithubBaseUrl){$env:UV_INSTALLER_GITHUB_BASE_URL=$selected.GithubBaseUrl}else{Remove-Item Env:UV_INSTALLER_GITHUB_BASE_URL -ErrorAction SilentlyContinue};$job=Start-Job -ArgumentList $selected.ScriptUrl -ScriptBlock {param($scriptUrl) $ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';Invoke-RestMethod -Uri $scriptUrl | Invoke-Expression};$elapsedSec=0;while($true){if(Wait-Job -Job $job -Timeout 1){break};$elapsedSec+=1;if($elapsedSec -ge $timeoutSec){Stop-Job -Job $job -ErrorAction SilentlyContinue | Out-Null;Remove-Job -Job $job -Force -ErrorAction SilentlyContinue | Out-Null;throw ('uv automatic installation timed out after {0} seconds.' -f $timeoutSec)};Write-Host ('[INFO] Downloading/installing uv... {0}s' -f $elapsedSec)};$null=Receive-Job -Job $job -Keep;$jobState=$job.State;Remove-Job -Job $job -Force -ErrorAction SilentlyContinue | Out-Null;if($jobState -ne 'Completed'){throw ('uv automatic installation failed with job state: {0}' -f $jobState)};Write-Host '[INFO] Download uv complete.'"
if errorlevel 1 (
  echo [ERROR] Failed to download uv automatically within 10 minutes.
  echo [ERROR] Please install uv manually, or place uv under: %UV_TOOL_DIR%
  exit /b 1
)
exit /b 0
