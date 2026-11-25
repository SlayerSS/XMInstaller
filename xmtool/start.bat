@echo off
cd /d "%~dp0"
python xmtool.py
if errorlevel 1 pause
