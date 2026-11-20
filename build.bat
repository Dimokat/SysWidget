@echo off
rem Builds SysWidget.exe and Setup.exe (author: Dimokat). The release zip ships them side by side.
rem The version is taken from the latest git tag (or %VERSION%), see stamp.ps1.
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
rem Windows' media session API ("Now playing") is described by the metadata files that ship with Windows 10/11.
set WINRT=/r:%WINDIR%\System32\WinMetadata\Windows.Media.winmd /r:%WINDIR%\System32\WinMetadata\Windows.Foundation.winmd /r:%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.dll
powershell -NoProfile -ExecutionPolicy Bypass -File stamp.ps1 || exit /b 1
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /win32icon:SysWidget.ico /resource:SysWidget.ico,SysWidget.ico /out:SysWidget.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll %WINRT% @obj\lang.rsp obj\SysWidget.cs Gpus.cs SettingsWindow.cs Extras.cs Popups.cs Lang.cs || exit /b 1
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /win32icon:SysWidget.ico /win32manifest:setup.manifest /resource:SysWidget.ico,SysWidget.ico /out:Setup.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll @obj\lang.rsp obj\Setup.cs Lang.cs || exit /b 1
echo Built SysWidget.exe and Setup.exe