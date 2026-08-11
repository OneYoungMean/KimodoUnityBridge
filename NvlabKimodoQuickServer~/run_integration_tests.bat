@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul

set "ROOT_DIR=%~dp0"
if "%ROOT_DIR:~-1%"=="\" set "ROOT_DIR=%ROOT_DIR:~0,-1%"
set "SCRIPT=%ROOT_DIR%\core\integration_test_suite.py"
set "PYTHON_EXE="
set "PYTHON_ARGS="

call :resolve_python
if not defined PYTHON_EXE (
  echo [ERROR] Could not find a host Python interpreter.
  exit /b 1
)

if not "%~1"=="" goto passthrough

echo.
echo Kimodo QuickServer Integration Tests
echo ====================================
echo.
"%PYTHON_EXE%" %PYTHON_ARGS% "%SCRIPT%" --list
echo.
set /p TEST_SELECTION=Select test id(s), range:T15-T20, tag:<name>, or testfull (empty = testfull): 
if "%TEST_SELECTION%"=="" set "TEST_SELECTION=testfull"

if /I "%TEST_SELECTION%"=="testfull" (
  "%PYTHON_EXE%" %PYTHON_ARGS% "%SCRIPT%" --full
  exit /b %ERRORLEVEL%
)

echo %TEST_SELECTION% | findstr /B /C:"range:" >nul
if not errorlevel 1 (
  set "RANGE_VALUE=%TEST_SELECTION:range:=%"
  for /f "tokens=1,2 delims=-" %%A in ("%RANGE_VALUE%") do (
    "%PYTHON_EXE%" %PYTHON_ARGS% "%SCRIPT%" --range "%%~A" "%%~B"
    exit /b !ERRORLEVEL!
  )
)

echo %TEST_SELECTION% | findstr /B /C:"tag:" >nul
if not errorlevel 1 (
  set "TAG_NAME=%TEST_SELECTION:tag:=%"
  "%PYTHON_EXE%" %PYTHON_ARGS% "%SCRIPT%" --tag "%TAG_NAME%"
  exit /b %ERRORLEVEL%
)

echo %TEST_SELECTION% | findstr /C:"," >nul
if not errorlevel 1 (
  "%PYTHON_EXE%" %PYTHON_ARGS% "%SCRIPT%" --cases "%TEST_SELECTION%"
  exit /b %ERRORLEVEL%
)

"%PYTHON_EXE%" %PYTHON_ARGS% "%SCRIPT%" --case "%TEST_SELECTION%"
exit /b %ERRORLEVEL%

:passthrough
"%PYTHON_EXE%" %PYTHON_ARGS% "%SCRIPT%" %*
exit /b %ERRORLEVEL%

:resolve_python
set "PYTHON_EXE="
set "PYTHON_ARGS="
where py >nul 2>nul
if not errorlevel 1 (
  set "PYTHON_EXE=py"
  set "PYTHON_ARGS=-3"
  goto :eof
)
where python >nul 2>nul
if not errorlevel 1 (
  set "PYTHON_EXE=python"
  goto :eof
)
goto :eof
