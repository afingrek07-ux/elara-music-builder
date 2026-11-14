; Установщик Elara Music Builder (Inno Setup 6)
; Сборка: ISCC.exe ElaraMusicBuilder.iss /DPublishDir=..\publish
; Установка в профиль пользователя — права администратора не нужны.

#ifndef MyAppVersion
  #define MyAppVersion "4.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

#define MyAppName "Elara Music Builder"
#define MyAppExe "ElaraMusicBuilder.exe"

[Setup]
AppId={{8C1B6F7A-4D2E-4F8B-9E3A-5E1A7A040001}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=Elara Music
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableWelcomePage=yes
DisableReadyPage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=ElaraMusicBuilder_Setup
SetupIconFile=..\src\ElaraMusicBuilder\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\docs\THIRD_PARTY_NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autoprograms}\Удалить {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Запустить {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Временные файлы и кэш. Проекты пользователя (.elara) и готовые видео не удаляются.
Type: filesandordirs; Name: "{localappdata}\ElaraMusicBuilder\cache"
Type: filesandordirs; Name: "{localappdata}\ElaraMusicBuilder\work"
