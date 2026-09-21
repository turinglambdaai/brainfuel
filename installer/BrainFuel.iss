; BrainFuel Windows installer (Inno Setup).
; Build via installer/build-installer.ps1 — it publishes win-x64 and passes
; /DAppVersion=<csproj Version>. Compiling manually defaults to 0.0.0-dev.
;
; Install model: per-user (VS Code style) — installs into
; {localappdata}\Programs\BrainFuel with no UAC prompt. This matches the app's
; own behavior: autostart lives in HKCU\...\Run and settings in %APPDATA%.

#ifndef AppVersion
#define AppVersion "0.0.0-dev"
#endif

[Setup]
AppId={{8D651A00-4A07-4C5C-AD1C-1A6988366992}
AppName=BrainFuel
AppVersion={#AppVersion}
AppVerName=BrainFuel {#AppVersion}
AppPublisher=TuringLambdaAI
AppPublisherURL=https://github.com/turinglambdaai
AppSupportURL=https://github.com/turinglambdaai/brainfuel/issues
AppUpdatesURL=https://github.com/turinglambdaai/brainfuel/releases
UninstallDisplayName=BrainFuel {#AppVersion}
UninstallDisplayIcon={app}\BrainFuel.exe
DefaultDirName={localappdata}\Programs\BrainFuel
DefaultGroupName=BrainFuel
LicenseFile=..\LICENSE
SetupIconFile=..\Assets\tray.ico
OutputDir=..\dist
OutputBaseFilename=BrainFuel-Setup-{#AppVersion}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The app holds a global single-instance mutex; let Restart Manager close it
; cleanly so the exe is not locked during (un)install.
CloseApplications=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "zh"; MessagesFile: "Translations\ChineseSimplified.isl"

[CustomMessages]
AutostartTask=Start BrainFuel automatically when Windows starts
zh.AutostartTask=开机时自动启动 BrainFuel
AutostartGroup=Additional tasks
zh.AutostartGroup=其他任务

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "{cm:AutostartTask}"; GroupDescription: "{cm:AutostartGroup}"

[Files]
Source: "..\publish\win-x64\BrainFuel.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\BrainFuel"; Filename: "{app}\BrainFuel.exe"
Name: "{group}\{cm:UninstallProgram,BrainFuel}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\BrainFuel"; Filename: "{app}\BrainFuel.exe"; Tasks: desktopicon

[Registry]
; Same value the in-app autostart toggle manages (HKCU Run), so the settings
; checkbox stays in sync; removed again on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "BrainFuel"; ValueData: """{app}\BrainFuel.exe"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\BrainFuel.exe"; Description: "{cm:LaunchProgram,BrainFuel}"; Flags: nowait postinstall skipifsilent

; Note: uninstall intentionally keeps user data (%APPDATA%\BrainFuel holds the
; API key and window position) so reinstalling or upgrading is non-destructive.
