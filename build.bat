@echo off
setlocal
set CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" ( echo Cannot find csc.exe. .NET Framework 4.8 is required. & pause & exit /b 1 )
set REF=%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8
if not exist "%REF%" ( echo Cannot find .NET 4.8 reference assemblies. & pause & exit /b 1 )
"%CSC%" /target:winexe /out:CaddyToolsWin.exe /win32icon:caddy.ico ^
  /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.ServiceProcess.dll ^
  /r:"%REF%\WindowsBase.dll" /r:"%REF%\PresentationCore.dll" /r:"%REF%\PresentationFramework.dll" /r:"%REF%\System.Xaml.dll" ^
  /r:lib\ICSharpCode.AvalonEdit.dll Program.cs
if errorlevel 1 ( echo Build failed. & pause & exit /b 1 )
copy /Y "lib\ICSharpCode.AvalonEdit.dll" "ICSharpCode.AvalonEdit.dll" >nul
if errorlevel 1 ( echo Failed to copy ICSharpCode.AvalonEdit.dll. & pause & exit /b 1 )
echo Build succeeded: CaddyToolsWin.exe (+ ICSharpCode.AvalonEdit.dll)
endlocal
