@echo off
rem Gate for the engine: tools\verify.cmd runs tools\verify.sh, the same
rem script pre-push runs. Build, test, secrets, hygiene - nothing else.
rem
rem Comments are 'rem' on purpose: '#' is not a comment for cmd.exe.

setlocal
set "SH=sh"
where sh >nul 2>nul || set "SH=%ProgramFiles%\Git\bin\sh.exe"
"%SH%" "%~dp0verify.sh"
exit /b %errorlevel%
