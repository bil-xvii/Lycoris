#define AppName "Lycoris"
#ifndef AppVersion
  #error Pass /DAppVersion=x.y.z from build-installer.ps1
#endif
#ifndef PublishDirectory
  #define PublishDirectory "..\..\publish"
#endif

[Setup]
AppId={{7E6D9A23-748C-4D62-9E36-6632D452A1F0}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Lycoris
DefaultDirName={localappdata}\Programs\Lycoris
DefaultGroupName=Lycoris
PrivilegesRequired=lowest
OutputDir={#PublishDirectory}
OutputBaseFilename=Lycoris-Setup-{#AppVersion}
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
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
Source: "{#PublishDirectory}\Lycoris.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Lycoris"; Filename: "{app}\Lycoris.exe"
Name: "{autodesktop}\Lycoris"; Filename: "{app}\Lycoris.exe"; Tasks: desktopicon

[Code]
const
  InvalidFileAttributes = $FFFFFFFF;
  ErrorFileNotFound = 2;
  ErrorPathNotFound = 3;

var DeleteData: Boolean;

function GetFileAttributesW(FileName: String): Cardinal;
external 'GetFileAttributesW@kernel32.dll stdcall uninstallonly';

function InitializeUninstall(): Boolean;
begin
  DeleteData := False;
  if not UninstallSilent then
    DeleteData := MsgBox('Удалить настройки, логи и кэш Lycoris из %LocalAppData%? Защищённый журнал в %ProgramData% останется. / Delete Lycoris settings, logs, and cache from %LocalAppData%? The protected journal in %ProgramData% will remain.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
  Attributes: Cardinal;
  ErrorCode: LongInt;
begin
  // Inno evaluates [UninstallDelete] checks during installation.
  if (CurUninstallStep <> usUninstall) or not DeleteData then
    Exit;

  DataDir := ExpandConstant('{localappdata}\Lycoris');
  Attributes := GetFileAttributesW(DataDir);
  if Attributes = Cardinal(InvalidFileAttributes) then begin
    ErrorCode := DLLGetLastError;
    if (ErrorCode = ErrorFileNotFound) or (ErrorCode = ErrorPathNotFound) then
      Exit;
    RaiseException('Не удалось проверить папку локальных данных Lycoris. / Could not inspect the local Lycoris data folder. Win32 ' + IntToStr(ErrorCode) + '.');
  end;
  if (Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then
    RaiseException('Путь к локальным данным Lycoris не является папкой. / The local Lycoris data path is not a folder.');

  if not DelTree(DataDir, True, True, True) then begin
    Log('Failed to remove local Lycoris data.');
    RaiseException('Не удалось полностью удалить локальные данные Lycoris. Закройте программы, использующие эти файлы, и повторите удаление. / Could not completely remove local Lycoris data. Close programs using these files and retry uninstalling.');
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var ResultCode: Integer;
begin
  Result := '';
  if Exec(ExpandConstant('{cmd}'), '/C tasklist /FI "IMAGENAME eq Lycoris.exe" /NH | find /I "Lycoris.exe" >NUL', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
    Result := 'Закройте Lycoris перед установкой. / Close Lycoris before installing.';
end;
