; Встановлювач NeoBar (Inno Setup 6). Збирається з release.bat; вручну: ISCC installer\NeoBar.iss
; Ставиться в профіль користувача (без прав адміністратора): поруч із програмою лежать її профілі,
; тому тека має бути доступна для запису - Program Files для цього не годиться.

#define AppName "NeoBar"
#define AppVersion "1.4.6"
#define AppPublisher "NeoCyberin"
#define AppExe "NeoBar.exe"
#ifndef SrcDir
  #define SrcDir "..\dist\release\NeoBar"
#endif

[Setup]
AppId={{FC8D9E7C-5720-4C49-8DF3-49F86BDD77F3}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
DefaultDirName={localappdata}\Programs\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename={#AppName}-{#AppVersion}-setup
SetupIconFile=..\res\NeoBar.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; поки панель працює, встановлювач і видалення просять її закрити
AppMutex=Local\NeoBarLauncher
CloseApplications=yes
#ifdef SIGN
SignTool=neobar
SignedUninstaller=yes
#endif

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "uk"; MessagesFile: "compiler:Languages\Ukrainian.isl"

[CustomMessages]
uk.DeleteData=Видалити також дані NeoBar?%n%nПрофілі й налаштування, нотатки, шпалери, звуки, резервні копії реєстру та карантин.%nЯкщо залишити, вони знову будуть доступні після повторного встановлення.
en.DeleteData=Also delete NeoBar data?%n%nProfiles and settings, notes, wallpapers, sounds, registry backups and quarantine.%nIf you keep them, they will be available again after reinstalling.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; прибрати сліди в системі: автозапуск, контекстне меню, шар Vulkan, обгортки в теках ігор, завдання адміністратора
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall"; RunOnceId: "NeoBarCleanup"; Flags: waituntilterminated

[UninstallDelete]
Type: files; Name: "{app}\NeoBarVkLayer*.json"
Type: files; Name: "{app}\neobar-error.log"
Type: filesandordirs; Name: "{app}\logs"
Type: filesandordirs; Name: "{app}\cache"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    if DirExists(ExpandConstant('{app}')) then
      if SuppressibleMsgBox(CustomMessage('DeleteData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
        DelTree(ExpandConstant('{app}'), True, True, True);
end;
