@echo off
rem Diagnoses the Windows client exiting at startup.
rem Builds and runs current main and the last known-good commit (235ffac),
rem capturing console output and the crash entries from the Windows Event Log,
rem then uploads the log to the Mac (default 192.168.178.20, or pass an IP as the first argument).
setlocal
cd /d "%~dp0"
set "LOG=%~dp0diagnose-windows.log"
set "GOOD=235ffac"
set "MAC=192.168.178.20"
if not "%~1"=="" set "MAC=%~1"
set "GOODDIR=%TEMP%\pw-%GOOD%"
set "APP=avalonia\PixelWizard.AvaloniaClient"
set "DLL=bin\Debug\net9.0-windows\PixelWizard.AvaloniaClient.dll"

echo Stopping running PixelWizard / dotnet processes...
taskkill /F /IM PixelWizard.AvaloniaClient.exe >nul 2>&1
taskkill /F /IM dotnet.exe >nul 2>&1

echo PixelWizard Windows diagnostics > "%LOG%"
echo ==== environment ==== >> "%LOG%"
git rev-parse --short HEAD >> "%LOG%" 2>&1
git status --short >> "%LOG%" 2>&1
dotnet --version >> "%LOG%" 2>&1
ver >> "%LOG%"
echo -- app data files -- >> "%LOG%"
dir /b "%APPDATA%\PixelWizardConnect" >> "%LOG%" 2>&1

echo.
echo [1/2] Building and running current main.
echo       If the window opens, close it to continue.
echo ==== main: build ==== >> "%LOG%"
dotnet build "%APP%" -nologo -v q >> "%LOG%" 2>&1
echo ==== main: run ==== >> "%LOG%"
dotnet "%APP%\%DLL%" >> "%LOG%" 2>&1
echo exit code: %ERRORLEVEL% >> "%LOG%"

echo.
echo [2/2] Building and running %GOOD% (last known-good).
echo       If the window opens, close it to continue.
if exist "%GOODDIR%" git worktree remove --force "%GOODDIR%" >nul 2>&1
git worktree add --detach "%GOODDIR%" %GOOD% >> "%LOG%" 2>&1
echo ==== %GOOD%: build ==== >> "%LOG%"
dotnet build "%GOODDIR%\%APP%" -nologo -v q >> "%LOG%" 2>&1
echo ==== %GOOD%: run ==== >> "%LOG%"
dotnet "%GOODDIR%\%APP%\%DLL%" >> "%LOG%" 2>&1
echo exit code: %ERRORLEVEL% >> "%LOG%"
git worktree remove --force "%GOODDIR%" >nul 2>&1

echo ==== event log, last 30 min ==== >> "%LOG%"
powershell -NoProfile -Command "Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='.NET Runtime','Application Error'; StartTime=(Get-Date).AddMinutes(-30)} -ErrorAction SilentlyContinue | Select-Object -First 6 | Format-List TimeCreated, ProviderName, Message | Out-String -Width 300" >> "%LOG%" 2>&1

rem Strip the user name and machine name before anything leaves this PC.
powershell -NoProfile -Command "$t = Get-Content -Raw -LiteralPath $env:LOG; $t = $t.Replace($env:USERPROFILE, '%%USERPROFILE%%'); if ($env:USERNAME.Length -ge 3) { $t = $t.Replace($env:USERNAME, '<user>') }; $t = $t.Replace($env:COMPUTERNAME, '<pc>'); Set-Content -LiteralPath $env:LOG -Value $t"

echo.
echo Done. Log: %LOG%
start "" notepad "%LOG%"
echo.
echo Sending the log to the Mac at %MAC%:8899 ...
curl.exe -sS --max-time 20 -T "%LOG%" http://%MAC%:8899/diagnose-windows.log
if errorlevel 1 echo Upload failed. Check the Mac is on the same network and the receiver is running.
endlocal
pause
