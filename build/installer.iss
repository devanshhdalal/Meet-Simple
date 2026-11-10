; Optional: builds a normal Setup.exe (Start menu shortcut, uninstaller, no admin rights needed).
; 1) From the repo root, run build\build.bat
; 2) Install Inno Setup from https://jrsoftware.org/isdl.php
; 3) Open this file and press Compile.
[Setup]
AppName=Meet Simple
AppVersion=0.1.3
DefaultDirName={localappdata}\Programs\Meet Simple
DefaultGroupName=Meet Simple
PrivilegesRequired=lowest
OutputBaseFilename=MeetSimple-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\MeetSimple.exe
; Paths below are relative to this .iss file (build\).
SourceDir=..

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\Meet Simple"; Filename: "{app}\MeetSimple.exe"
Name: "{autodesktop}\Meet Simple"; Filename: "{app}\MeetSimple.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Run]
Filename: "{app}\MeetSimple.exe"; Description: "Start Meet Simple"; Flags: nowait postinstall skipifsilent
