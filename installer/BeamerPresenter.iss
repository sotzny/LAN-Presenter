#define MyAppName "Beamer Presenter for LAN-Parties"
#define MyAppPublisher "Danny Sotzny"
#define MyAppExeName "BeamerPresenter.App.exe"
#ifndef MyAppId
  #define MyAppId "C29A17C4-DC3A-4E06-9721-2A3CFD4F1AC4"
#endif
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#ifndef ArtifactDir
  #define ArtifactDir "..\artifacts"
#endif

[Setup]
AppId={{{#MyAppId}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#ArtifactDir}
OutputBaseFilename=BeamerPresenter-{#MyAppVersion}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
SetupMutex=BeamerPresenter.Setup.{#MyAppId}

[Files]
Source: "{#PublishDir}\BeamerPresenter.Updater.exe"; Flags: dontcopy
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Desktop-Verknüpfung erstellen"; GroupDescription: "Zusätzliche Verknüpfungen:"; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{#MyAppName} starten"; Flags: nowait postinstall skipifsilent

[Code]
var
  UpdateGuardFile: String;

function GetCurrentProcessId(): LongWord;
  external 'GetCurrentProcessId@kernel32.dll stdcall';

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode, Attempt: Integer;
  GuardFile: String;
  Status: AnsiString;
begin
  Result := '';
  ExtractTemporaryFile('BeamerPresenter.Updater.exe');
  GuardFile := ExpandConstant('{tmp}\presenter-shutdown.json');
  UpdateGuardFile := GuardFile;
  DeleteFile(GuardFile);
  DeleteFile(GuardFile + '.release');
  DeleteFile(GuardFile + '.done');
  if not Exec(ExpandConstant('{tmp}\BeamerPresenter.Updater.exe'),
    '--guard "' + ExpandConstant('{app}\{#MyAppExeName}') + '" "' + GuardFile + '" ' + IntToStr(GetCurrentProcessId()) + ' ' + ExpandConstant('{param:UpdateHelper|0}'),
    '', SW_HIDE, ewNoWait, ExitCode) then
  begin
    Result := 'Die Anwendung konnte vor dem Update nicht beendet werden.';
    Exit;
  end;
  for Attempt := 1 to 450 do
  begin
    if LoadStringFromFile(GuardFile, Status) then
    begin
      if Trim(Status) <> '0' then
        Result := 'Die Anwendung läuft noch. Die Installation wurde abgebrochen.';
      Exit;
    end;
    Sleep(100);
  end;
  Result := 'Das Beenden der Anwendung hat zu lange gedauert. Es wurden keine Dateien ersetzt.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Attempt: Integer;
begin
  if (CurStep = ssPostInstall) and (UpdateGuardFile <> '') then
  begin
    SaveStringToFile(UpdateGuardFile + '.release', 'true', False);
    for Attempt := 1 to 100 do
    begin
      if FileExists(UpdateGuardFile + '.done') then Exit;
      Sleep(100);
    end;
    RaiseException('Die Update-Sperre konnte nicht freigegeben werden. Bitte die Anwendung nach dem Setup manuell starten.');
  end;
end;
