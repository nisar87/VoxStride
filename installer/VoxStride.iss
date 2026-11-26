; VoxStride per-user installer (Inno Setup 6). No administrator rights required.
; Build: ISCC /DAppVersion=0.1.0 /DSourceDir=<publish folder> installer\VoxStride.iss

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-x64"
#endif

[Setup]
AppId={{6A3F1C2E-8B7D-4E59-9C41-5D2E7A9B0F13}
AppName=VoxStride
AppVersion={#AppVersion}
AppPublisher=VoxStride
AppPublisherURL=https://github.com/nisar87/VoxStride
AppSupportURL=https://github.com/nisar87/VoxStride/issues
DefaultDirName={localappdata}\Programs\VoxStride
DefaultGroupName=VoxStride
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\artifacts\installer
OutputBaseFilename=VoxStride-Setup-{#AppVersion}-x64
SetupIconFile=..\src\VoxStride.Desktop\Assets\voxstride.ico
UninstallDisplayIcon={app}\VoxStride.exe
UninstallDisplayName=VoxStride
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "startup"; Description: "Start VoxStride when I sign in"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\VoxStride"; Filename: "{app}\VoxStride.exe"
Name: "{userdesktop}\VoxStride"; Filename: "{app}\VoxStride.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "VoxStride"; ValueData: """{app}\VoxStride.exe"" --background"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\VoxStride.exe"; Description: "Launch VoxStride"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM VoxStride.exe /F"; Flags: runhidden; RunOnceId: "StopVoxStride"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
  RC: Integer;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\VoxStride');
    if DirExists(DataDir) then
    begin
      if UninstallSilent or (MsgBox('Also remove VoxStride settings, history, undo data and the VoxStride browser profile?' + #13#10 +
                                    '(API keys in Windows Credential Manager are removed too.)', mbConfirmation, MB_YESNO) = IDYES) then
      begin
        DelTree(DataDir, True, True, True);
      end;
    end;
    // Remove saved provider keys (generic credentials named VoxStride/*).
    Exec(ExpandConstant('{sys}\cmdkey.exe'), '/delete:VoxStride/provider/claude', '', SW_HIDE, ewWaitUntilTerminated, RC);
    Exec(ExpandConstant('{sys}\cmdkey.exe'), '/delete:VoxStride/provider/gemini', '', SW_HIDE, ewWaitUntilTerminated, RC);
    Exec(ExpandConstant('{sys}\cmdkey.exe'), '/delete:VoxStride/provider/openai', '', SW_HIDE, ewWaitUntilTerminated, RC);
  end;
end;
