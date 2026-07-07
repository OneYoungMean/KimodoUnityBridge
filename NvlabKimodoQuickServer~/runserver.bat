@echo off
setlocal EnableExtensions
call "%~dp0run_server.bat" --hold-cli %*
exit /b %ERRORLEVEL%
