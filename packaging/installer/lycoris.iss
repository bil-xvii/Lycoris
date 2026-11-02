#define AppName "Lycoris"
#ifndef AppVersion
  #error Pass /DAppVersion=x.y.z from build-installer.ps1
#endif

[Setup]
AppId={{7E6D9A23-748C-4D62-9E36-6632D452A1F0}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Lycoris
DefaultDirName={localappdata}\Programs\Lycoris
DefaultGroupName=Lycoris
PrivilegesRequired=lowest
OutputDir=..\..\publish
OutputBaseFilename=Lycoris-Setup-{#AppVersion}
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
UninstallDisplayIcon={app}\Lycoris.exe
#ifdef Sign
SignTool=LycorisSign
#endif
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=Lycoris.exe

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе / Create a desktop shortcut"; GroupDescription: "Дополнительно / Additional icons:"; Flags: unchecked

[Files]
Source: "..\..\publish\Lycoris.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Lycoris"; Filename: "{app}\Lycoris.exe"
Name: "{autodesktop}\Lycoris"; Filename: "{app}\Lycoris.exe"; Tasks: desktopicon

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Lycoris"; Check: ShouldDeleteData

[Code]
var DeleteData: Boolean;

function InitializeUninstall(): Boolean;
begin
  DeleteData := False;
  if UninstallSilent then
    Result := True
  else
    Result := MsgBox('Удалить настройки, логи и кэш Lycoris из %LocalAppData%? Защищённый журнал в %ProgramData% останется. / Delete Lycoris settings, logs, and cache from %LocalAppData%? The protected journal in %ProgramData% will remain.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

function ShouldDeleteData(): Boolean;
begin
  Result := DeleteData;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var ResultCode: Integer;
begin
  Result := '';
  if Exec(ExpandConstant('{cmd}'), '/C tasklist /FI "IMAGENAME eq Lycoris.exe" /NH | find /I "Lycoris.exe" >NUL', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
    Result := 'Закройте Lycoris перед установкой. / Close Lycoris before installing.';
end;
