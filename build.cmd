@echo off
rem Builds bin\wslim.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WPF=%FW%\WPF
cd /d "%~dp0"
if not exist bin mkdir bin
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /langversion:5 /out:bin\wslim.exe /win32icon:assets\wslim.ico /win32manifest:src\app.manifest /resource:src\agent.sh,agent.sh /resource:assets\wslim.ico,wslim.ico /r:"%WPF%\PresentationFramework.dll" /r:"%WPF%\PresentationCore.dll" /r:"%WPF%\WindowsBase.dll" /r:"%FW%\System.Xaml.dll" /r:"%FW%\System.Xml.dll" src\App.cs
if errorlevel 1 exit /b 1
echo built bin\wslim.exe
