@echo off
REM ==========================================================
REM  Durango - server launcher (double-click me)
REM  Keep user-facing menu text in tools\start-server.ps1, not in this batch file.
REM ==========================================================
title Durango - server launcher
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\start-server.ps1"
if errorlevel 1 (
  echo.
  echo [!] start-server.ps1 failed to run - see the message above.
  pause
)
