@echo off
cd /d "%~dp0"
title FC 27 DLSS Switch - rebuild EXE
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
echo.
pause
