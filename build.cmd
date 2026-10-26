@echo off
setlocal
cd /d "%~dp0"
set FWDIR=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
"%FWDIR%\csc.exe" /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ ^
  /out:AIRestarter.exe ^
  /win32icon:app.ico ^
  /win32manifest:app.manifest ^
  /r:"%FWDIR%\System.Windows.Forms.dll" /r:"%FWDIR%\System.Drawing.dll" /r:"%FWDIR%\System.Core.dll" /r:"%FWDIR%\System.Xml.dll" ^
  AIRestarter.cs
if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
echo BUILD OK
