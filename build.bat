@echo off
rem Compile DualSenseBattery.exe avec le compilateur C# fourni par Windows (.NET Framework 4.x).
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /out:DualSenseBattery.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll DualSenseBattery.cs
