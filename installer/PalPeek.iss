#define MyAppName "PalPeek"
#define MyAppVersion "0.4.3"
#define MyAppPublisher "PalPeek 开源项目"
#define MyAppExeName "PalPeek.exe"
#ifndef PublishDir
#define PublishDir "..\artifacts\publish"
#endif

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
UsePreviousAppDir=yes
CloseApplications=yes
RestartApplications=no
Uninstallable=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=PalPeek-Setup-{#MyAppVersion}-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE
SetupIconFile=..\src\PalPeek.App\Assets\palpeek.ico
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD_PARTY.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\PalPeek"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 PalPeek"; Filename: "{uninstallexe}"
Name: "{autodesktop}\PalPeek"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Registry]
Root: HKLM; Subkey: "Software\Classes\palpeek"; ValueType: string; ValueData: "URL:PalPeek Protocol"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\palpeek"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKLM; Subkey: "Software\Classes\palpeek\DefaultIcon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKLM; Subkey: "Software\Classes\palpeek\shell\open\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "PalPeek"; Flags: deletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PalPeek"; ValueData: """{app}\{#MyAppExeName}"" --background"; Flags: uninsdeletevalue

[Run]
Filename: "{cmd}"; Parameters: "/c netsh advfirewall firewall add rule name=""PalPeek Tailnet API"" dir=in action=allow program=""{app}\{#MyAppExeName}"" protocol=TCP localport=48191 remoteip=100.64.0.0/10,fd7a:115c:a1e0::/48 profile=any"; Flags: runhidden; StatusMsg: "正在配置 PalPeek 防火墙规则…"
Filename: "{cmd}"; Parameters: "/c netsh advfirewall firewall add rule name=""PalPeek Host TCP"" dir=in action=allow protocol=TCP localport=47984,47989,47990,48010 remoteip=100.64.0.0/10,fd7a:115c:a1e0::/48 profile=any"; Flags: runhidden
Filename: "{cmd}"; Parameters: "/c netsh advfirewall firewall add rule name=""PalPeek Host UDP"" dir=in action=allow protocol=UDP localport=47998-48000,48010 remoteip=100.64.0.0/10,fd7a:115c:a1e0::/48 profile=any"; Flags: runhidden
Filename: "{app}\{#MyAppExeName}"; Description: "启动 PalPeek"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c netsh advfirewall firewall delete rule name=""PalPeek Tailnet API"""; Flags: runhidden; RunOnceId: "DeleteTailnetApiFirewallRule"
Filename: "{cmd}"; Parameters: "/c netsh advfirewall firewall delete rule name=""PalPeek Host TCP"""; Flags: runhidden; RunOnceId: "DeleteHostTcpFirewallRule"
Filename: "{cmd}"; Parameters: "/c netsh advfirewall firewall delete rule name=""PalPeek Host UDP"""; Flags: runhidden; RunOnceId: "DeleteHostUdpFirewallRule"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\PalPeek\sunshine-runtime"
Type: filesandordirs; Name: "{localappdata}\PalPeek\moonlight-profile"
