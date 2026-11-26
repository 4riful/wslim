@echo off
rem Builds bin\wslim.exe (window) and bin\wslim-cli.exe (console) with the C# compiler that ships
rem with Windows (.NET Framework 4.8). Both share src\Core.cs and src\Compact.cs. Also builds bin\wslim-tests.exe.
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WPF=%FW%\WPF
cd /d "%~dp0"
if not exist bin mkdir bin
rem The agent script is embedded gzip-compressed (about 7 KB instead of 20 KB).
powershell -NoProfile -ExecutionPolicy Bypass -Command "$b=[IO.File]::ReadAllBytes('src\agent.sh'); $f=[IO.File]::Create('bin\agent.sh.gz'); $z=New-Object IO.Compression.GZipStream($f,[IO.Compression.CompressionLevel]::Optimal); $z.Write($b,0,$b.Length); $z.Close(); $f.Close()"
if errorlevel 1 exit /b 1
set COMMON=/nologo /optimize+ /langversion:5 /win32manifest:src\app.manifest /resource:bin\agent.sh.gz,agent.sh.gz
"%FW%\csc.exe" %COMMON% /target:winexe /out:bin\wslim.exe /win32icon:assets\wslim.ico /resource:assets\wslim.ico,wslim.ico /r:"%WPF%\PresentationFramework.dll" /r:"%WPF%\PresentationCore.dll" /r:"%WPF%\WindowsBase.dll" /r:"%FW%\System.Xaml.dll" /r:"%FW%\System.Xml.dll" src\App.cs src\Scramble.cs src\Core.cs src\Compact.cs
if errorlevel 1 exit /b 1
echo built bin\wslim.exe
rem The console exe gets the same icon with only its small frames (a console never shows 64 or 256 px).
powershell -NoProfile -ExecutionPolicy Bypass -File tools\small-icon.ps1 -In assets\wslim.ico -Out bin\wslim-cli.ico
if errorlevel 1 exit /b 1
"%FW%\csc.exe" %COMMON% /target:exe /out:bin\wslim-cli.exe /win32icon:bin\wslim-cli.ico src\Cli.cs src\Core.cs src\Compact.cs
if errorlevel 1 exit /b 1
del bin\agent.sh.gz bin\wslim-cli.ico
echo built bin\wslim-cli.exe
rem The tests: plain console program, same compiler, no test framework. Run it without arguments for the unit tests.
"%FW%\csc.exe" /nologo /optimize+ /langversion:5 /target:exe /out:bin\wslim-tests.exe tests\Tests.cs src\Core.cs src\Compact.cs
if errorlevel 1 exit /b 1
echo built bin\wslim-tests.exe
