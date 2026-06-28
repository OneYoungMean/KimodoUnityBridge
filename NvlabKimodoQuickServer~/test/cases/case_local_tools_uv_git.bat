@echo off
setlocal EnableExtensions
set "SCRIPT_DIR=%~dp0"
set "RESULT_FILE=%~1"
if not defined RESULT_FILE set "RESULT_FILE=%TEMP%\kimodo_case_local_tools_%RANDOM%%RANDOM%.txt"
for %%I in ("%SCRIPT_DIR%\..\..") do set "ROOT_DIR=%%~fI"
set "UV_EXE=%ROOT_DIR%\program\exe\uv\uv.exe"
set "GIT_EXE=%ROOT_DIR%\program\exe\git\cmd\git.exe"
set "LFS_EXE=%ROOT_DIR%\program\exe\git\mingw32\bin\git-lfs.exe"

set "RC=0"
if not exist "%UV_EXE%" set "RC=1"
if not exist "%GIT_EXE%" set "RC=1"
if not exist "%LFS_EXE%" set "RC=1"

if "%RC%"=="0" (
  "%UV_EXE%" --version >nul 2>nul || set "RC=1"
)
if "%RC%"=="0" (
  "%GIT_EXE%" --version >nul 2>nul || set "RC=1"
)
if "%RC%"=="0" (
  "%LFS_EXE%" version >nul 2>nul || set "RC=1"
)

> "%RESULT_FILE%" (
  echo CASE_NAME=local_tools_uv_git
  if "%RC%"=="0" (
    echo STATUS=PASS
    echo DETAIL=ok
  ) else (
    echo STATUS=FAIL
    echo DETAIL=local_tools_failed_rc_%RC%
  )
)
exit /b %RC%
