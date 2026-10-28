@echo off
rem Diagnoses the Windows client exiting at startup.
rem Builds and runs current main and the last known-good commit (235ffac),
rem capturing console output and the crash entries from the Windows Event Log.
setlocal
cd /d "%~dp0"
set "LOG=%~dp0diagnose-windows.log"
set "GOOD=235ffac"
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

rem The repo is public: strip the user name and machine name before anything leaves this PC.
powershell -NoProfile -Command "$t = Get-Content -Raw -LiteralPath $env:LOG; $t = $t.Replace($env:USERPROFILE, '%%USERPROFILE%%'); if ($env:USERNAME.Length -ge 3) { $t = $t.Replace($env:USERNAME, '<user>') }; $t = $t.Replace($env:COMPUTERNAME, '<pc>'); Set-Content -LiteralPath $env:LOG -Value $t"

echo.
echo Done. Log: %LOG%
start "" notepad "%LOG%"
echo.
choice /M "Push the log to GitHub (public repo) so Claude can read it"
if errorlevel 2 goto :end
git add -f diagnose-windows.log
git commit -m "diag: Windows startup log" -- diagnose-windows.log
git pull --rebase
git push
:end
endlocal
pause
