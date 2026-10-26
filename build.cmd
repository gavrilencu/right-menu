@echo off
rem Double-click to build bin\RadialMenu.exe and start it.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -Run
if errorlevel 1 pause
