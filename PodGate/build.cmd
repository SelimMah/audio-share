@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo Le SDK .NET est introuvable. Installe-le avec :
  echo     winget install Microsoft.DotNet.SDK.10
  echo puis rouvre ce terminal et relance build.cmd.
  exit /b 1
)

echo Compilation de PodGate...
dotnet publish src\PodGate.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -o dist
if errorlevel 1 (
  echo.
  echo La compilation a echoue : copie le message d'erreur ci-dessus.
  exit /b 1
)

echo.
echo OK : dist\PodGate.exe
endlocal
