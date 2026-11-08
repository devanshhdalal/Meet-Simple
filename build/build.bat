@echo off
setlocal EnableExtensions
cd /d "%~dp0\.."

rem Prefer the 64-bit .NET (some PCs list the 32-bit copy in Program Files (x86) first, and it has no SDK).
if exist "%ProgramW6432%\dotnet\dotnet.exe" set "PATH=%ProgramW6432%\dotnet;%PATH%"

rem Stop early with a clear message if only the .NET runtime (not the SDK) is installed.
dotnet --list-sdks 2>nul | findstr /r "^[0-9]" >nul
if errorlevel 1 (
  echo.
  echo No .NET SDK was found. Install the ".NET 8 SDK" ^(the SDK, not the Runtime^) from:
  echo   https://dotnet.microsoft.com/download/dotnet/8.0
  echo Then CLOSE this window, open a new one, and run build\build.bat again.
  echo To check, run:  dotnet --list-sdks
  if /i "%MEETSIMPLE_BUILD_NOPAUSE%"=="1" exit /b 1
  pause
  exit /b 1
)

rem A running copy locks publish files and makes publish fail with MSB3027.
rem taskkill returns before Windows releases the files, so wait (up to 15 s) until the process is really gone.
tasklist /FI "IMAGENAME eq MeetSimple.exe" 2>nul | find /I "MeetSimple.exe" >nul
if errorlevel 1 goto :app_closed
echo Closing running Meet Simple so publish files can be overwritten...
taskkill /IM MeetSimple.exe /F >nul 2>&1
set /a close_wait_seconds=0
:wait_for_app_exit
tasklist /FI "IMAGENAME eq MeetSimple.exe" 2>nul | find /I "MeetSimple.exe" >nul
if errorlevel 1 goto :app_closed
set /a close_wait_seconds+=1
if %close_wait_seconds% geq 15 goto :app_closed
timeout /t 1 /nobreak >nul
goto :wait_for_app_exit
:app_closed

rem Old files must never end up in the zip, so the output is deleted for real (retrying while Windows lets go of it).
rem The folder itself can stay locked by a terminal or File Explorer window open inside it even when every file is
rem gone; an empty folder is just as clean, so only leftover files count as a failure.
echo Cleaning previous publish output...
set /a clean_attempts=0
:clean_publish
if exist "publish" rmdir /s /q "publish" 2>nul
if not exist "publish" goto :publish_clean
dir /b /a "publish" 2>nul | findstr "^" >nul
if errorlevel 1 goto :publish_clean
set /a clean_attempts+=1
if %clean_attempts% geq 5 (
  echo.
  echo Could not delete the old files in the publish folder. Close anything using them ^(for example Meet Simple^)
  echo and run build\build.bat again.
  goto :fail
)
timeout /t 2 /nobreak >nul
goto :clean_publish
:publish_clean
if not exist "publish" mkdir publish

rem A shareable build always starts from scratch: an interrupted earlier build can leave WPF's intermediate files
rem half-written (error BG1002 "...baml cannot be found"), and incremental builds would trust them.
echo Clearing intermediate build files...
if exist "src\MeetSimple\obj" rmdir /s /q "src\MeetSimple\obj" 2>nul
if exist "src\MeetSimple\bin" rmdir /s /q "src\MeetSimple\bin" 2>nul

echo Building Meet Simple (self-contained single-file, so users need nothing installed)...
rem Single-file packs managed assemblies into MeetSimple.exe. Whisper native libs must stay on disk
rem under runtimes\ (IncludeNativeLibrariesForSelfExtract=false) so Whisper.net can find them.
dotnet publish src\MeetSimple\MeetSimple.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishReadyToRun=true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=false ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=None ^
  -p:DebugSymbols=false ^
  -o publish || goto :fail

echo Trimming unused Whisper runtimes (keeping win-x64 + Vulkan win-x64 only)...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Trim-Publish.ps1" -PublishDir "%~dp0..\publish" || goto :fail

echo Creating a zip you can share...
if exist "MeetSimple-win-x64.zip" del /f /q "MeetSimple-win-x64.zip"
powershell -NoProfile -Command ^
  "$stage = Join-Path $env:TEMP ('MeetSimple-zip-' + [guid]::NewGuid().ToString('N'));" ^
  "New-Item -ItemType Directory -Path (Join-Path $stage 'MeetSimple') | Out-Null;" ^
  "Copy-Item -Path 'publish\*' -Destination (Join-Path $stage 'MeetSimple') -Recurse -Force;" ^
  "Compress-Archive -Path (Join-Path $stage 'MeetSimple') -DestinationPath 'MeetSimple-win-x64.zip' -Force;" ^
  "Remove-Item -LiteralPath $stage -Recurse -Force" || goto :fail

echo.
echo SHA-256 of the zip (publish this next to the download so people can verify it):
certutil -hashfile MeetSimple-win-x64.zip SHA256

echo.
echo Done.
echo   Try it:   publish\MeetSimple.exe
echo   Share it: MeetSimple-win-x64.zip  (unzip, open the MeetSimple folder, run MeetSimple.exe)
if /i "%MEETSIMPLE_BUILD_NOPAUSE%"=="1" exit /b 0
pause
exit /b 0

:fail
echo.
echo Build failed - scroll up for the first error and send it to Claude.
if /i "%MEETSIMPLE_BUILD_NOPAUSE%"=="1" exit /b 1
pause
exit /b 1
