@echo off
chcp 65001 >nul
rem Checks the usage lookup once and prints the numbers (never the login token).
set "EXE=%LOCALAPPDATA%\Programs\ClaudeNiang\ClaudeNiang.exe"
if not exist "%EXE%" set "EXE=%~dp0build\ClaudeNiang.exe"
"%EXE%" --probe
for %%I in ("%EXE%") do type "%%~dpIstate\probe.txt"
pause
