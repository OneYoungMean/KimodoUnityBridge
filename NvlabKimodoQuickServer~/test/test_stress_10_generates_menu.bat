@echo off
setlocal EnableExtensions

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
set "CUDA_PS1=%SCRIPT_DIR%\test_stress_10_generates_cuda.ps1"
set "CPU_PS1=%SCRIPT_DIR%\test_stress_10_generates_cpu.ps1"

if not exist "%CUDA_PS1%" (
  echo [ERROR] Missing script: %CUDA_PS1%
  exit /b 1
)
if not exist "%CPU_PS1%" (
  echo [ERROR] Missing script: %CPU_PS1%
  exit /b 1
)

set "SEL=%~1"
if "%SEL%"=="" (
  echo.
  echo ===== Kimodo Stress Test Menu =====
  echo 1. CUDA (10 generates)
  echo 2. CPU  (10 generates)
  echo.
  set /p "SEL=Select 1 or 2: "
)

if /I "%SEL%"=="1" goto run_cuda
if /I "%SEL%"=="2" goto run_cpu

echo [ERROR] Invalid selection: %SEL%
echo Usage: %~nx0 [1^|2]
exit /b 1

:run_cuda
echo [INFO] Running CUDA stress test...
powershell -NoProfile -ExecutionPolicy Bypass -File "%CUDA_PS1%"
exit /b %ERRORLEVEL%

:run_cpu
echo [INFO] Running CPU stress test...
powershell -NoProfile -ExecutionPolicy Bypass -File "%CPU_PS1%"
exit /b %ERRORLEVEL%

