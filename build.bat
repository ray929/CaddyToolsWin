@echo off
setlocal
set CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" ( echo Cannot find csc.exe. .NET Framework 4.8 is required. & pause & exit /b 1 )
"%CSC%" /target:winexe /out:CaddyToolsWin.exe /win32icon:caddy.ico /res:caddy.ico /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.ServiceProcess.dll /r:lib\FastColoredTextBox.dll Program.cs
if errorlevel 1 ( echo Build failed. & pause & exit /b 1 )
copy /Y "lib\FastColoredTextBox.dll" "FastColoredTextBox.dll" >nul
if errorlevel 1 ( echo Failed to copy FastColoredTextBox.dll. & pause & exit /b 1 )
echo Build succeeded: CaddyToolsWin.exe (+ FastColoredTextBox.dll)
endlocal
