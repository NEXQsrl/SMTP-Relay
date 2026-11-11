; NEXQ SMTP Relay - setup (Inno Setup 6)
; Built by build\Release.proj, which passes AppVersion, PublishDir, OutputDir and, for signed builds, Sign.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif

#define AppName "NEXQ SMTP Relay"
#define AppPublisher "NEXQ S.r.l."
#define AppUrl "https://github.com/NEXQsrl/SMTP-Relay"
#define ServiceExe "SmtpRelay.exe"
#define ConsoleExe "SmtpRelayManager.exe"
#define FirewallRule "NEXQ SMTP Relay"

[Setup]
AppId={{8C5B6E0A-3F7D-4C1E-9A52-6D1F0B7E4A21}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://www.nexq.it
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
AppCopyright=Copyright (c) NEXQ S.r.l.
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}
DefaultDirName={autopf}\NEXQ\SMTP Relay
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=SmtpRelay-Setup-{#AppVersion}
SetupIconFile=..\assets\brand\app.ico
WizardImageFile=..\assets\brand\wizard-large.bmp
WizardSmallImageFile=..\assets\brand\wizard-small.bmp
UninstallDisplayIcon={app}\{#ConsoleExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.14393
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
#ifdef Sign
SignTool=nexq
SignedUninstaller=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"

[CustomMessages]
english.TaskFirewall=Allow incoming connections to the relay in Windows Firewall (domain and private networks)
italian.TaskFirewall=Consenti le connessioni in ingresso al relay in Windows Firewall (reti di dominio e private)
english.TaskDesktop=Create a desktop shortcut for the management console
italian.TaskDesktop=Crea un collegamento sul desktop per la console di gestione
english.StatusService=Registering the Windows service...
italian.StatusService=Registrazione del servizio Windows...
english.StatusFirewall=Configuring Windows Firewall...
italian.StatusFirewall=Configurazione di Windows Firewall...
english.StatusStart=Starting the service...
italian.StatusStart=Avvio del servizio...
english.StatusStop=Stopping the service...
italian.StatusStop=Arresto del servizio...
english.RunConsole=Open the SMTP Relay console to configure the relay
italian.RunConsole=Apri la console di SMTP Relay per configurare il relay
english.ConsoleName=SMTP Relay Console
italian.ConsoleName=Console SMTP Relay
english.ServiceFailed=The files were installed, but the Windows service could not be registered.%n%nIf a security product blocked the program (for example the Microsoft Defender rule that blocks new executables), allow the installation folder as described in the README, then open the SMTP Relay console and choose "Install the service".
italian.ServiceFailed=I file sono stati installati, ma la registrazione del servizio Windows ha dato errore.%n%nSe un prodotto di sicurezza ha bloccato il programma (ad esempio la regola di Microsoft Defender che blocca i nuovi eseguibili), consenti la cartella di installazione come descritto nel README, poi apri la console di SMTP Relay e scegli "Install the service".
english.RemoveData=Do you also want to delete the settings, the stored secrets, the logs and the copies of undelivered messages?%n%nChoose No to keep them for a later installation.
italian.RemoveData=Vuoi eliminare anche le impostazioni, i segreti salvati, i log e le copie dei messaggi non consegnati?%n%nScegli No per conservarli per una installazione successiva.

[Tasks]
Name: "firewall"; Description: "{cm:TaskFirewall}"
Name: "desktopicon"; Description: "{cm:TaskDesktop}"; Flags: unchecked

[Files]
; The settings file is created on the first installation only and is never replaced or removed by the setup.
Source: "{#PublishDir}\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "appsettings.json,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{cm:ConsoleName}"; Filename: "{app}\{#ConsoleExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{cm:ConsoleName}"; Filename: "{app}\{#ConsoleExe}"; Tasks: desktopicon

[Run]
; The service itself is registered in [Code] (RegisterService), so that a failure can be reported.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden; Tasks: firewall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#FirewallRule}"" dir=in action=allow program=""{app}\{#ServiceExe}"" protocol=TCP profile=domain,private enable=yes"; StatusMsg: "{cm:StatusFirewall}"; Flags: runhidden; Tasks: firewall
; An update starts the service again only if it was running before.
Filename: "{app}\{#ServiceExe}"; Parameters: "start"; StatusMsg: "{cm:StatusStart}"; Flags: runhidden; Check: WasServiceRunning
Filename: "{app}\{#ConsoleExe}"; Description: "{cm:RunConsole}"; Flags: postinstall nowait skipifsilent runascurrentuser
; After an update started from the console (silent), the console is opened again.
Filename: "{app}\{#ConsoleExe}"; Flags: nowait runascurrentuser; Check: ShouldRelaunchConsole

[UninstallRun]
Filename: "{app}\{#ServiceExe}"; Parameters: "uninstall"; Flags: runhidden; RunOnceId: "RemoveService"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden; RunOnceId: "RemoveFirewallRule"

[Code]
var
  ServiceWasRunning: Boolean;

function WasServiceRunning: Boolean;
begin
  Result := ServiceWasRunning;
end;

function ShouldRelaunchConsole: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;

{ Before the files are replaced: remember whether the relay was running and stop it.
  "SmtpRelay.exe status" returns 0 only when the service is running. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExePath: String;
  ResultCode: Integer;
begin
  Result := '';
  ServiceWasRunning := False;
  ExePath := ExpandConstant('{app}\{#ServiceExe}');
  if FileExists(ExePath) then
  begin
    if Exec(ExePath, 'status', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      ServiceWasRunning := (ResultCode = 0);
    if ServiceWasRunning then
    begin
      WizardForm.PreparingLabel.Caption := CustomMessage('StatusStop');
      if (not Exec(ExePath, 'stop', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
        Result := 'The SMTP Relay service could not be stopped. Stop it from the Services console and run the setup again.';
    end;
  end;
end;

{ Registers the service (or points the existing one to the new files), protects the data folder and
  registers the event log source. A failure is reported instead of leaving a program that silently has no service. }
procedure RegisterService;
var
  ResultCode: Integer;
  Started: Boolean;
begin
  WizardForm.StatusLabel.Caption := CustomMessage('StatusService');
  Started := Exec(ExpandConstant('{app}\{#ServiceExe}'), 'install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if Started and (ResultCode = 0) then
    Log('Service registered.')
  else
  begin
    Log(Format('Service registration failed (exit code %d).', [ResultCode]));
    if not WizardSilent then
      MsgBox(CustomMessage('ServiceFailed'), mbError, MB_OK);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    RegisterService;
end;

{ Removing the program keeps the data by default; the administrator may choose to delete it. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if (not UninstallSilent) and
       (MsgBox(CustomMessage('RemoveData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
    begin
      DelTree(ExpandConstant('{commonappdata}\SmtpRelay'), True, True, True);
      DeleteFile(ExpandConstant('{app}\appsettings.json'));
      RemoveDir(ExpandConstant('{app}'));
    end;
  end;
end;
