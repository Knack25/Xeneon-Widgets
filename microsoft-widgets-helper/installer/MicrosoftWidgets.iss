#ifndef ReleaseVersion
  #define ReleaseVersion "0.3.2"
#endif
#ifndef HelperSource
  #define HelperSource "..\dist\helper"
#endif
#ifndef ReleaseOutput
  #define ReleaseOutput "..\..\dist\release"
#endif
#ifndef HelperManifest
  #error HelperManifest must point to the generated exact installer file list.
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
OutputDir={#ReleaseOutput}
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
#include HelperManifest

[Icons]
Name: "{group}\Microsoft Widgets Setup"; Filename: "{app}\MicrosoftWidgets.Helper.exe"; WorkingDir: "{app}"
Name: "{group}\Stop Microsoft Widgets Helper"; Filename: "{app}\MicrosoftWidgets.Helper.exe"; Parameters: "--stop"; WorkingDir: "{app}"
Name: "{group}\Uninstall Microsoft Widgets"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Microsoft Widgets Setup"; Filename: "{app}\MicrosoftWidgets.Helper.exe"; Tasks: desktopicon; WorkingDir: "{app}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MicrosoftWidgetsHelper"; ValueData: """{app}\MicrosoftWidgets.Helper.exe"" --no-browser"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\MicrosoftWidgets.Helper.exe"; Description: "Open Microsoft Widgets setup"; Flags: nowait postinstall skipifsilent; Check: not IsHelperUpdate

[Code]
function IsHelperUpdate: Boolean;
begin
  Result := ExpandConstant('{param:HELPERUPDATE|0}') <> '0';
end;

function RequestHelperStop: Boolean;
forward;

function InitializeSetup: Boolean;
begin
  Result := not IsAdmin;
  if not Result then
    MsgBox('Microsoft Widgets is a per-user app. Close this installer and run it normally; do not use "Run as administrator".', mbError, MB_OK);
end;

function InitializeUninstall: Boolean;
begin
  if IsAdmin then
  begin
    Result := False;
    MsgBox('Microsoft Widgets must be removed by the signed-in user. Close this uninstaller and run it normally; do not use "Run as administrator".', mbError, MB_OK);
    Exit;
  end;

  Result := RequestHelperStop;
  if not Result then
    MsgBox('Microsoft Widgets Helper could not be stopped safely. Close it from the notification area, then try uninstalling again.', mbError, MB_OK)
  else
    Sleep(1000);
end;

function RequestHelperStop: Boolean;
var
  ResultCode: Integer;
  PowerShellPath: String;
  StopScript: String;
  Parameters: String;
begin
  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  StopScript := ExpandConstant('{app}\Stop-MicrosoftWidgetsHelper.ps1');
  if not FileExists(StopScript) then
  begin
    ExtractTemporaryFile('Stop-MicrosoftWidgetsHelper.ps1');
    StopScript := ExpandConstant('{tmp}\Stop-MicrosoftWidgetsHelper.ps1');
  end;
  Parameters := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + StopScript + '"';
  Result := Exec(PowerShellPath, Parameters,
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not RequestHelperStop then
    Result := 'Microsoft Widgets Helper could not be stopped safely. Close it from the notification area and try again.'
  else
    Sleep(1000);
end;
