@echo off
setlocal
rem Compile ModeIA.exe avec le compilateur C# fourni par Windows (pas de SDK requis).

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo Compilateur C# introuvable : .NET Framework 4.x est requis.
    exit /b 1
)

cd /d "%~dp0"
"%CSC%" -nologo -codepage:65001 -target:winexe -platform:anycpu -optimize+ ^
    -out:ModeIA.exe -win32manifest:app.manifest ^
    -r:System.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll ^
    ModeIA.cs
if errorlevel 1 (
    echo.
    echo Echec de la compilation.
    exit /b 1
)

echo ModeIA.exe compile avec succes.
