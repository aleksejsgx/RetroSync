@echo off
rem RetroSync 2.2 - build with the C# compiler included in Windows (.NET Framework 4.x)
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /out:RetroSync.exe /win32icon:app.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Web.Extensions.dll /resource:retrosync-pair.elf,retrosync-pair.elf /resource:retrosync-rest.elf,retrosync-rest.elf Program.cs Settings.cs Ftp.cs SyncCore.cs RetroArchSync.cs Ps2CardSync.cs Mirror.cs Theme.cs Controls.cs RemoteWake.cs Activity.cs Modules.cs MainWindow.cs DashboardPage.cs RetroArchPage.cs Ps2Page.cs DolphinSync.cs DolphinPage.cs BackupPage.cs SettingsPage.cs LogPage.cs Demo.cs PairHelper.cs RestMode.cs Lang.cs
set RC=%ERRORLEVEL%
if not "%1"=="nopause" pause
exit /b %RC%
