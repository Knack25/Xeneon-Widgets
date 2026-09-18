#ifndef ReleaseVersion
  #define ReleaseVersion "0.3.2"
#endif
#ifndef HelperSource
  #define HelperSource "..\dist\helper"
#endif

[Setup]
AppId={{C7DA283D-675F-4467-B340-E24B5572C955}
AppName=Microsoft Widgets
AppVersion={#ReleaseVersion}
AppPublisher=Knack25
AppPublisherURL=https://github.com/Knack25/Xeneon-Widgets
AppSupportURL=https://github.com/Knack25/Xeneon-Widgets/issues
DefaultDirName={localappdata}\Programs\Microsoft Widgets
DefaultGroupName=Microsoft Widgets
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
OutputDir=..\..\dist\release
OutputBaseFilename=MicrosoftWidgetsSetup-{#ReleaseVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\MicrosoftWidgets.Helper\Assets\helper.ico
UninstallDisplayIcon={app}\MicrosoftWidgets.Helper.exe
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: "startup"; Description: "Start the helper when I sign in to Windows"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#HelperSource}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\docs\INSTALL.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Microsoft Widgets Setup"; Filename: "{app}\MicrosoftWidgets.Helper.exe"; WorkingDir: "{app}"
Name: "{group}\Stop Microsoft Widgets Helper"; Filename: "{app}\MicrosoftWidgets.Helper.exe"; Parameters: "--stop"; WorkingDir: "{app}"
Name: "{group}\Uninstall Microsoft Widgets"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Microsoft Widgets Setup"; Filename: "{app}\MicrosoftWidgets.Helper.exe"; Tasks: desktopicon; WorkingDir: "{app}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MicrosoftWidgetsHelper"; ValueData: """{app}\MicrosoftWidgets.Helper.exe"" --no-browser"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\MicrosoftWidgets.Helper.exe"; Description: "Open Microsoft Widgets setup"; Flags: nowait postinstall skipifsilent; Check: not IsHelperUpdate

[UninstallRun]
Filename: "{app}\MicrosoftWidgets.Helper.exe"; Parameters: "--stop"; Flags: runhidden waituntilterminated; RunOnceId: "StopHelper"

[Code]
function IsHelperUpdate: Boolean;
begin
  Result := ExpandConstant('{param:HELPERUPDATE|0}') <> '0';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  HelperPath: String;
begin
  Result := '';
  HelperPath := ExpandConstant('{app}\MicrosoftWidgets.Helper.exe');
  if FileExists(HelperPath) then
  begin
    Exec(HelperPath, '--stop', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1000);
  end;
end;
