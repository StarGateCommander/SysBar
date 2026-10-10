@echo off
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319

rem 1) program
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /platform:x64 /codepage:65001 ^
  /out:"%~dp0SysBar.exe" /win32manifest:"%~dp0app.manifest" ^
  /r:"%FW%\WPF\PresentationFramework.dll" /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\WindowsBase.dll" ^
  /r:System.Xaml.dll /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  "%~dp0SysBar.cs"
if errorlevel 1 (echo BLAD kompilacji SysBar & exit /b 1)
echo OK: %~dp0SysBar.exe

rem 2) instalator z wbudowanym SysBar.exe
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /platform:x64 /codepage:65001 ^
  /out:"%~dp0SysBarSetup.exe" /resource:"%~dp0SysBar.exe",SysBar.exe ^
  /r:System.Windows.Forms.dll "%~dp0Setup.cs"
if errorlevel 1 (echo BLAD kompilacji instalatora & exit /b 1)
echo OK: %~dp0SysBarSetup.exe
