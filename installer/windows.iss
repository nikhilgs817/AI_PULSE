#define AppVersion "0.3.0"
#ifndef SourceDir
  #define SourceDir "..\build\win-x64"
#endif
[Setup]
AppId={{944B3F21-E2CB-4B10-A8AF-7E2629D5AD27}
AppName=AI Pulse
AppVersion={#AppVersion}
AppPublisher=AI Pulse contributors
AppPublisherURL=https://github.com/nikhilgs817/AI_PULSE
AppSupportURL=https://github.com/nikhilgs817/AI_PULSE/issues
DefaultDirName={localappdata}\Programs\AI Pulse
DefaultGroupName=AI Pulse
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\build\releases
OutputBaseFilename=AI-Pulse-Setup-0.3.0-x64
SetupIconFile=..\src\AiPulse\Assets\pulse.ico
UninstallDisplayIcon={app}\AiPulse.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
VersionInfoVersion=0.3.0.0

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\AI Pulse"; Filename: "{app}\AiPulse.exe"; Parameters: "{code:GetDataArguments}"
Name: "{group}\Uninstall AI Pulse"; Filename: "{uninstallexe}"
Name: "{autodesktop}\AI Pulse"; Filename: "{app}\AiPulse.exe"; Parameters: "{code:GetDataArguments}"; Tasks: desktopicon

[Run]
Filename: "{app}\AiPulse.exe"; Parameters: "{code:GetDataArguments}"; Description: "Open AI Pulse"; Flags: nowait postinstall skipifsilent

[Code]
function GetDataArguments(Param: String): String;
var DataDir: String;
begin
  DataDir := ExpandConstant('{param:DATA_DIR|}');
  if DataDir = '' then DataDir := GetPreviousData('DataDir', '');
  if DataDir = '' then Result := ''
  else Result := '--data-dir "' + DataDir + '"';
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
var DataDir: String;
begin
  DataDir := ExpandConstant('{param:DATA_DIR|}');
  if DataDir = '' then DataDir := GetPreviousData('DataDir', '');
  SetPreviousData(PreviousDataKey, 'DataDir', DataDir);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var RunValue: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'AI Pulse', RunValue) then
      if Pos(Lowercase(ExpandConstant('{app}\AiPulse.exe')), Lowercase(RunValue)) > 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'AI Pulse');
end;
