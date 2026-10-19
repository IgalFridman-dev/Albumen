@echo off
rem Supported entry point: launching Albumen.App.exe directly bypasses package verification.
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0Verify-AlbumenRelease.ps1" %*
exit /b %ERRORLEVEL%
