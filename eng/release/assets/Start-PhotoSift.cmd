@echo off
rem Supported entry point: launching PhotoSift.App.exe directly bypasses package verification.
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0Verify-PhotoSiftRelease.ps1" %*
exit /b %ERRORLEVEL%
