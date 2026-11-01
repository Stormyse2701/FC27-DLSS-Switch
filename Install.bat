@echo off
net session >nul 2>&1
if %errorlevel% neq 0 (
  powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%~f0'"
  exit /b
)
cd /d "%~dp0"
title FC 27 DLSS Mod - Install
powershell -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0files\install.ps1"
echo.
pause
