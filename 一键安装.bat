@echo off
rem VibeMate one-click installer launcher. All logic lives in install.ps1 (kept ASCII here:
rem cmd parses .bat with the OEM codepage, non-ASCII bytes would break on other locales).
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
