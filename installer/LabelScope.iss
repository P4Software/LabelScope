; LabelScope installer (Inno Setup 6).
; Per-user install: no administrator rights are needed, and because LabelScope keeps
; settings.json and its logs next to the program, the folder must stay writable.
; %LocalAppData%\Programs is writable by the user, Program Files is not.
; The printer itself is added from inside the app (it asks for administrator rights then).

#ifndef AppVersion
  #define AppVersion "0.3.1"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-x64"
#endif

[Setup]
; Fixed GUID so a newer installer upgrades the old install instead of installing beside it.
AppId={{B6F0C2B4-7D55-4C57-9E43-3A0E7B6A41C1}
AppName=LabelScope
AppVersion={#AppVersion}
AppPublisher=P4 Software
DefaultDirName={localappdata}\Programs\LabelScope
DefaultGroupName=LabelScope
PrivilegesRequired=lowest
OutputDir=..\publish\installer
OutputBaseFilename=LabelScope-Setup
SetupIconFile=..\assets\labelscope.ico
UninstallDisplayIcon={app}\LabelScope.exe
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
DisableProgramGroupPage=yes

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion; Excludes: "*.pdb,settings.json,logs\*"

[Icons]
Name: "{group}\LabelScope"; Filename: "{app}\LabelScope.exe"
Name: "{autodesktop}\LabelScope"; Filename: "{app}\LabelScope.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Run]
Filename: "{app}\LabelScope.exe"; Description: "Start LabelScope"; Flags: nowait postinstall skipifsilent
; The in-app updater runs this installer silently with /relaunch=1. The entry above is skipped when
; silent, so the new version is started by this one instead.
Filename: "{app}\LabelScope.exe"; Flags: nowait; Check: IsRelaunchRequested

[Code]
{ True when the in-app updater asked for the new version to be opened after a silent install. }
function IsRelaunchRequested: Boolean;
begin
  Result := ExpandConstant('{param:relaunch|0}') = '1';
end;

// settings.json and logs are created by the app at run time, so they are not removed on
// purpose: uninstalling must never throw away the user's configuration.
