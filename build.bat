@echo off
rem Builds SysWidget.exe and Setup.exe (author: Dimokat). The release zip ships them side by side.
rem The version is taken from the latest git tag (or %VERSION%), see stamp.ps1.
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
powershell -NoProfile -ExecutionPolicy Bypass -File stamp.ps1 || exit /b 1
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /win32icon:SysWidget.ico /resource:SysWidget.ico,SysWidget.ico /out:SysWidget.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll obj\SysWidget.cs Gpus.cs SettingsWindow.cs || exit /b 1
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /win32icon:SysWidget.ico /win32manifest:setup.manifest /resource:SysWidget.ico,SysWidget.ico /out:Setup.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll obj\Setup.cs || exit /b 1
echo Built SysWidget.exe and Setup.exe