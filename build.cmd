@echo off
rem Builds ForzaTriggers.exe and ForzaTriggers.Diag.exe into .\dist using the C# compiler that ships with
rem .NET Framework 4 (already present on every Windows 10/11 PC). No Visual Studio, no NuGet, no SDK.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" ( echo csc.exe from .NET Framework 4 not found & exit /b 1 )
if not exist dist mkdir dist

"%CSC%" /nologo /nowarn:169 /target:winexe /optimize+ /platform:x64 /out:dist\ForzaTriggers.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll src\*.cs || exit /b 1
"%CSC%" /nologo /nowarn:169 /target:exe /define:SELFTEST /optimize+ /platform:x64 /out:dist\ForzaTriggers.Diag.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll src\*.cs || exit /b 1
copy /y docs\README.txt dist\README.txt >nul
echo Built: dist\ForzaTriggers.exe, dist\ForzaTriggers.Diag.exe
