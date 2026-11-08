@echo off
setlocal
cd /d "%~dp0"

rem Prefer the 64-bit .NET (some PCs list the 32-bit copy in Program Files (x86) first, and it has no SDK).
if exist "%ProgramW6432%\dotnet\dotnet.exe" set "PATH=%ProgramW6432%\dotnet;%PATH%"

rem Stop early with a clear message if only the .NET runtime (not the SDK) is installed.
dotnet --list-sdks 2>nul | findstr /r "^[0-9]" >nul
if errorlevel 1 (
  echo.
  echo No .NET SDK was found. Install the ".NET 8 SDK" ^(the SDK, not the Runtime^) from:
  echo   https://dotnet.microsoft.com/download/dotnet/8.0
  echo Then CLOSE this window, open a new one, and run build.bat again.
  echo To check, run:  dotnet --list-sdks
  pause
  exit /b 1
)

echo Building Meet Simple (self-contained, so users need nothing installed)...
dotnet publish MeetSimple.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o publish || goto :fail

echo Creating a zip you can share...
powershell -NoProfile -Command "Compress-Archive -Path 'publish\*' -DestinationPath 'MeetSimple-win-x64.zip' -Force" || goto :fail

echo.
echo SHA-256 of the zip (publish this next to the download so people can verify it):
certutil -hashfile MeetSimple-win-x64.zip SHA256

echo.
echo Done.
echo   Try it:   publish\MeetSimple.exe
echo   Share it: MeetSimple-win-x64.zip  (unzip anywhere, run MeetSimple.exe)
pause
exit /b 0

:fail
echo.
echo Build failed - scroll up for the first error and send it to Claude.
pause
exit /b 1
