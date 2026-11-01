@echo off
rem Builds SysWidget.exe and the single-file installer SysWidgetSetup.exe (author: Dimokat).
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /win32icon:SysWidget.ico /out:SysWidget.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll SysWidget.cs || exit /b 1
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /win32icon:SysWidget.ico /win32manifest:setup.manifest /resource:SysWidget.exe,SysWidget.exe /resource:SysWidget.ico,SysWidget.ico /out:SysWidgetSetup.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll Setup.cs || exit /b 1
echo Built SysWidget.exe and SysWidgetSetup.exe
