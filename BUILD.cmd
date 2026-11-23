@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo Install the .NET 8 SDK or Visual Studio with .NET desktop development first.
  echo See README.md for the build and PowerFab compatibility requirements.
  pause
  exit /b 1
)

if not exist "lib\Tekla.PowerFab.Extension.dll" (
  echo Copy the .NET 8-compatible Tekla.PowerFab.Extension.dll from your PowerFab installation
  echo into the lib folder, then run BUILD.cmd again.
  echo Check README.md before choosing the interface DLL.
  pause
  exit /b 1
)

dotnet build "Hanlon.PowerFab.Nesting.csproj" --configuration Release
if errorlevel 1 (
  echo Build failed. Read the errors above.
  pause
  exit /b 1
)

echo.
echo Built: bin\Release\net8.0-windows\Hanlon.PowerFab.Nesting.dll
echo Follow README.md to install and test it in PowerFab.
pause
