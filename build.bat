@echo off
setlocal
set CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" ( echo Cannot find csc.exe. .NET Framework 4.8 is required. & pause & exit /b 1 )
"%CSC%" /target:winexe /out:CaddyToolsWin.exe /win32icon:caddy.ico /res:caddy.ico /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.ServiceProcess.dll Program.cs
if errorlevel 1 ( echo Build failed. & pause & exit /b 1 )
echo Build succeeded: CaddyToolsWin.exe
endlocal
