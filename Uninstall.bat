@echo off
net session >nul 2>&1
if %errorlevel% neq 0 (
  powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%~f0'"
  exit /b
)
cd /d "%~dp0"
title FC 27 DLSS Mod - Uninstall
powershell -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0files\uninstall.ps1"
echo.
pause
