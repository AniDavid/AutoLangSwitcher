@echo off
rem Builds AutoLangSwitcher.exe with the C# compiler that is already part of Windows (.NET Framework 4.x).
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo Could not find csc.exe. Enable ".NET Framework 4.8" in "Turn Windows features on or off".
    pause
    exit /b 1
)

"%CSC%" /nologo /target:winexe /optimize+ /out:AutoLangSwitcher.exe ^
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
    /resource:Help\help-apps.png,help-apps.png /resource:Help\help-general.png,help-general.png ^
    /resource:Help\help-off.png,help-off.png /resource:Help\help-rules.png,help-rules.png ^
    /resource:Help\help-tray.png,help-tray.png ^
    *.cs

if errorlevel 1 (
    echo.
    echo Build FAILED - copy the errors above and send them to me.
    pause
    exit /b 1
)

rem The app used to be called KeyboardMemory; remove the old binary so it is not started by mistake.
if exist KeyboardMemory.exe del KeyboardMemory.exe 2>nul

echo.
echo Built AutoLangSwitcher.exe - starting it now. Look for the blue keyboard icon in the tray.
start "" "%~dp0AutoLangSwitcher.exe"
timeout /t 4 >nul
