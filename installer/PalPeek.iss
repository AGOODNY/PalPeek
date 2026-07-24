#define MyAppName "PalPeek"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "PalPeek contributors"
#define MyAppExeName "PalPeek.exe"

[Setup]
AppId={{CFBE7EF2-E0BB-4DBA-9418-28AE8D8D7F8D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\PalPeek
DefaultGroupName=PalPeek
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename=PalPeek-Setup-{#MyAppVersion}-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\{#MyAppExeName}

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD_PARTY.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\PalPeek"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\PalPeek"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Registry]
Root: HKCU; Subkey: "Software\Classes\palpeek"; ValueType: string; ValueData: "URL:PalPeek Protocol"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\palpeek"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\palpeek\DefaultIcon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\palpeek\shell\open\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PalPeek"; ValueData: """{app}\{#MyAppExeName}"" --background"; Flags: uninsdeletevalue

[Run]
Filename: "{cmd}"; Parameters: "/c netsh advfirewall firewall add rule name=""PalPeek Tailnet API"" dir=in action=allow program=""{app}\{#MyAppExeName}"" protocol=TCP localport=48191 remoteip=100.64.0.0/10,fd7a:115c:a1e0::/48 profile=any"; Flags: runhidden; StatusMsg: "正在配置 PalPeek 防火墙规则…"
Filename: "{cmd}"; Parameters: "/c netsh advfirewall firewall add rule name=""PalPeek Host TCP"" dir=in action=allow program=""{app}\runtime\sunshine\sunshine.exe"" protocol=TCP localport=47984,47989,47990,48010 remoteip=100.64.0.0/10,fd7a:115c:a1e0::/48 profile=any"; Flags: runhidden
Filename: "{cmd}"; Parameters: "/c netsh advfirewall firewall add rule name=""PalPeek Host UDP"" dir=in action=allow program=""{app}\runtime\sunshine\sunshine.exe"" protocol=UDP localport=47998-48000,48010 remoteip=100.64.0.0/10,fd7a:115c:a1e0::/48 profile=any"; Flags: runhidden
Filename: "{app}\{#MyAppExeName}"; Description: "启动 PalPeek"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c netsh advfirewall firewall delete rule name=""PalPeek Tailnet API"""; Flags: runhidden
Filename: "{cmd}"; Parameters: "/c netsh advfirewall firewall delete rule name=""PalPeek Host TCP"""; Flags: runhidden
Filename: "{cmd}"; Parameters: "/c netsh advfirewall firewall delete rule name=""PalPeek Host UDP"""; Flags: runhidden
