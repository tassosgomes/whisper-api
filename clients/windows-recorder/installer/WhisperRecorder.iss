; Instalador do Whisper Recorder (Inno Setup 6). Instala só para o usuário atual, sem direitos de administrador.
; Compilar: ISCC.exe WhisperRecorder.iss [/DAppVersion=1.2.3] [/DPublishDir=<pasta do dotnet publish>]
; ou, de preferência, installer\build-installer.ps1 (publica e compila).

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
; Saída do `dotnet publish` self-contained do Host, relativa a esta pasta.
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif
#define AppName "Whisper Recorder"
#define AppExe "WhisperRecorder.exe"
; Mesmo nome do mutex de instância única do app (UninstallDataPurge.AppMutexName).
#define AppMutexName "Whisper.Recorder.Host"
; Mesmo argumento do modo de remoção de dados do app (UninstallDataPurge.Argument).
#define PurgeArgument "--purge-data"

[Setup]
; GUID fixo: identifica o app para atualizar por cima e desinstalar. Não trocar entre versões.
AppId={{8F2B6C1E-4D7A-4E53-9A0B-3C5D1E7F2A94}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Whisper
; Sempre por usuário: sem PrivilegesRequiredOverridesAllowed o instalador nunca oferece "para todos os usuários".
PrivilegesRequired=lowest
; Com `lowest`, {autopf} é %LOCALAPPDATA%\Programs: não exige elevação e fica fora de Program Files.
DefaultDirName={autopf}\Whisper Recorder
DisableProgramGroupPage=yes
; Só Windows 11 (build 22000) e x64; o app é publicado para win-x64.
MinVersion=10.0.22000
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Instalador e desinstalador não rodam com o app aberto; fecham-no se o usuário aceitar.
AppMutex={#AppMutexName}
CloseApplications=yes
RestartApplications=no
SetupIconFile=..\assets\icon\icon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=..\artifacts\installer
OutputBaseFilename=WhisperRecorder-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Só o programa. Gravações, transcrições, configurações e a chave ficam em %LOCALAPPDATA%\Whisper\Recorder e no
; Credential Manager, criados pelo app: o instalador nunca os inclui e o desinstalador só os apaga se o usuário pedir.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  DataFolderHint = '%LOCALAPPDATA%\Whisper\Recorder';

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  // usUninstall vem antes de remover os arquivos: o WhisperRecorder.exe ainda existe para apagar os dados.
  if CurUninstallStep <> usUninstall then
    Exit;
  // Desinstalação silenciosa sempre mantém os dados: apagar exige uma decisão explícita do usuário.
  if UninstallSilent then
    Exit;

  // Padrão (primeiro botão) = manter.
  if MsgBox('Manter as gravações e transcrições neste PC?' + #13#10 + #13#10 +
      'Sim: os dados ficam e voltam a aparecer se você reinstalar o app.' + #13#10 +
      'Não: apaga gravações, transcrições, configurações e a chave de acesso. Não é possível desfazer.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON1) = IDYES then
    Exit;

  if not Exec(ExpandConstant('{app}\{#AppExe}'), '{#PurgeArgument}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    ResultCode := -1;
  if ResultCode = 0 then
    Exit;

  // 2 = o app foi aberto de novo durante a desinstalação: nada foi apagado.
  if ResultCode = 2 then
    MsgBox('O Whisper Recorder está aberto, e os dados não foram apagados.' + #13#10 +
        'Feche o app e apague a pasta ' + DataFolderHint + ' (cole no Explorador de Arquivos) ' +
        'e a credencial "Whisper.Recorder/ApiKey" no Gerenciador de Credenciais do Windows.',
        mbError, MB_OK)
  else
    MsgBox('Não foi possível apagar todos os dados automaticamente.' + #13#10 +
        'Para removê-los, apague a pasta ' + DataFolderHint + ' (cole no Explorador de Arquivos) ' +
        'e a credencial "Whisper.Recorder/ApiKey" no Gerenciador de Credenciais do Windows.',
        mbError, MB_OK);
end;
